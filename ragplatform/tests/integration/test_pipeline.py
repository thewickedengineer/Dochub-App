"""
Phase 1 + 2 exit criteria against real services: Postgres + pgvector, Dochub's
queue table, Azurite, and the real local embedding model.
"""

from __future__ import annotations

import uuid

import httpx
import psycopg
import pytest

from conftest import enqueue, process_request, queue_row, upload_fixtures
from fakes import FailingEmbedder, RecordingLLM
from rag_ingest.dochub.callbacks import DochubCallbacks
from rag_ingest.pipeline.build import build_runner
from rag_ingest.queue.postgres import COMPLETED, DEAD_LETTERED, IN_FLIGHT, PostgresQueue
from rag_ingest.search import SearchRequest, search
from rag_ingest.storage.blob import BlobStore
from rag_ingest.storage.migrate import migrate
from rag_ingest.storage.repository import Repository
from rag_ingest.worker import Worker

pytestmark = pytest.mark.integration

INDEXABLE = ["README.md", "rating.py", "FnolService.cs", "notes.txt"]
REJECTED = ["package-lock.json", "policy.pdf"]


@pytest.fixture
async def stack(settings, local_embedder, fake_dochub):
    migrate(settings)
    repo = Repository(settings)
    queue = PostgresQueue(settings)
    blobs = callbacks = None
    try:
        await repo.open()
        blobs = BlobStore(settings)
        callbacks = DochubCallbacks(settings, transport=httpx.MockTransport(fake_dochub.handler))

        def make_worker(embedder=local_embedder, llm=None):
            return Worker(settings, queue, repo, build_runner(settings, blobs, repo, embedder, llm), callbacks,
                          embedder)

        yield {"repo": repo, "queue": queue, "blobs": blobs, "worker": make_worker, "settings": settings}
        async with repo.pool.connection() as conn:
            await conn.execute("TRUNCATE documents, chunks, pipeline_runs CASCADE")
        async with await psycopg.AsyncConnection.connect(settings.dochub_queue_dsn) as conn:
            await conn.execute("TRUNCATE dochub.queue_messages")
            await conn.commit()
    finally:
        # Closed even when setup fails: an open pool keeps the event loop alive.
        await queue.close()
        if callbacks is not None:
            await callbacks.close()
        if blobs is not None:
            await blobs.close()
        await repo.close()


async def _run_one(stack, worker=None):
    lease = await stack["queue"].receive()
    assert lease is not None, "nothing was on the queue"
    await (worker or stack["worker"]()).handle(lease)


async def _chunks(repo: Repository) -> list[tuple]:
    async with repo.pool.connection() as conn:
        return await (await conn.execute(
            "SELECT chunk_id, document_id, created_at FROM chunks ORDER BY chunk_id")).fetchall()


# ── Phase 1: queue contract ──────────────────────────────────────────────────

async def test_a_message_is_claimed_once_and_completed(stack):
    await enqueue(stack["settings"], {"hello": "world"})
    queue = stack["queue"]

    lease = await queue.receive()
    assert lease.body == {"hello": "world"} and lease.delivery_count == 1
    # SKIP LOCKED: a second consumer gets nothing while it is held.
    assert await queue.receive() is None

    await lease.complete()
    assert (await queue_row(stack["settings"]))["status"] == COMPLETED


async def test_an_abandoned_message_waits_out_its_backoff(stack):
    await enqueue(stack["settings"], {"x": 1})
    lease = await stack["queue"].receive()
    await lease.abandon("dependency down")

    row = await queue_row(stack["settings"])
    assert row["status"] == IN_FLIGHT and row["error"] == "dependency down"
    assert await stack["queue"].receive() is None   # still inside its 15s window


async def test_an_unreadable_message_is_dead_lettered(stack):
    await enqueue(stack["settings"], {"not": "a process request"})
    await _run_one(stack)
    row = await queue_row(stack["settings"])
    assert row["status"] == DEAD_LETTERED and "unreadable" in row["error"]


# ── Phase 2: end to end ──────────────────────────────────────────────────────

async def test_a_batch_is_indexed_and_every_document_reported(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], INDEXABLE + REJECTED, container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)

    await _run_one(stack)

    assert len(fake_dochub.paths("/processed")) == len(INDEXABLE) + 1   # + the artifact call
    failed = [body for path, body in fake_dochub.calls if path.endswith("/failed")]
    assert len(failed) == len(REJECTED)
    assert all(body["permanent"] for body in failed)
    reasons = " ".join(body["error"] for body in failed)
    # policy.pdf is a PDF with no pages: Docling refuses it, and that is permanent.
    assert "lock file" in reasons and "extraction_failed" in reasons

    # The artifact callback comes last, once everything is settled.
    assert fake_dochub.calls[-1][0] == f"/api/ingestion/artifacts/{request['artifactId']}/processed"
    assert fake_dochub.calls[-1][1] == {"sourceDocumentId": request["sourceDocumentId"]}
    assert (await queue_row(stack["settings"]))["status"] == COMPLETED

    processed = [body for path, body in fake_dochub.calls if "/documents/" in path and path.endswith("/processed")]
    assert all(body["chunkCount"] > 0 and not body["skipped"] for body in processed)
    assert {body["documentVersionId"] for body in processed} <= {d["documentVersionId"] for d in documents}

    chunks = await _chunks(stack["repo"])
    assert len({c[1] for c in chunks}) == len(INDEXABLE)


async def test_re_enqueueing_the_same_batch_produces_no_new_chunks(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], INDEXABLE, container)
    request = process_request(documents, container)

    await enqueue(stack["settings"], request)
    await _run_one(stack)
    before = await _chunks(stack["repo"])

    fake_dochub.calls.clear()
    await enqueue(stack["settings"], request)
    await _run_one(stack)

    assert await _chunks(stack["repo"]) == before   # same ids, same timestamps
    processed = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert len(processed) == len(INDEXABLE) and all(b["skipped"] for b in processed)


async def test_a_new_version_replaces_the_documents_chunks(stack, fake_dochub, tmp_path):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    [first] = await upload_fixtures(stack["settings"], ["notes.txt"], container)
    await enqueue(stack["settings"], process_request([first], container))
    await _run_one(stack)
    old = await _chunks(stack["repo"])

    # Same Dochub document, new bytes and a new version — what a sync produces.
    from azure.storage.blob.aio import BlobServiceClient
    import base64, hashlib
    data = b"Reserves are now set within three working days.\n\nThis replaces the five-day rule entirely."
    service = BlobServiceClient.from_connection_string(stack["settings"].blob_connection_string.get_secret_value())
    path = first["blobPath"].replace("20261001T000000000Z", "20261002T000000000Z")
    await service.get_blob_client(container, path).upload_blob(data, overwrite=True)
    await service.close()
    second = {**first, "blobPath": path, "revision": 2, "documentVersionId": str(uuid.uuid4()),
              "contentMd5": base64.b64encode(hashlib.md5(data).digest()).decode(), "sizeBytes": len(data)}
    await enqueue(stack["settings"], process_request([second], container))
    await _run_one(stack)

    new = await _chunks(stack["repo"])
    assert {c[1] for c in new} == {c[1] for c in old}      # same RAG document
    assert [c[2] for c in new] != [c[2] for c in old]      # rewritten chunks
    async with stack["repo"].pool.connection() as conn:
        texts = [r[0] for r in await (await conn.execute("SELECT text FROM chunks")).fetchall()]
    assert any("three working days" in t for t in texts)
    assert not any("five working days" in t for t in texts)


async def test_a_blob_that_does_not_match_dochubs_hash_is_refused(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    [document] = await upload_fixtures(stack["settings"], ["notes.txt"], container)
    document["contentMd5"] = "AAAAAAAAAAAAAAAAAAAAAA=="
    await enqueue(stack["settings"], process_request([document], container))
    await _run_one(stack)

    [(path, body)] = [(p, b) for p, b in fake_dochub.calls if p.endswith("/failed")]
    assert body["permanent"] and "content_hash_mismatch" in body["error"]


# ── Search ───────────────────────────────────────────────────────────────────

async def test_search_finds_an_exact_identifier_and_a_paraphrase(stack, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], INDEXABLE, container)
    await enqueue(stack["settings"], process_request(documents, container))
    await _run_one(stack)

    principals = ["org:org-test"]

    keyword = await search(stack["repo"], local_embedder, SearchRequest(
        tenant_id="org-test", principals=principals, query="territory_factor", top_k=3))
    assert keyword and keyword[0].filename == "rating.py"
    assert keyword[0].lexical_rank == 1

    paraphrase = await search(stack["repo"], local_embedder, SearchRequest(
        tenant_id="org-test", principals=principals,
        query="how quickly do we have to respond when someone reports a claim", top_k=3))
    assert any("24 hours" in hit.text for hit in paraphrase)
    assert paraphrase[0].heading_path  # cited with its section

    # ACL filtering happens in SQL: a principal from another org sees nothing.
    other = await search(stack["repo"], local_embedder, SearchRequest(
        tenant_id="org-test", principals=["org:someone-else"], query="territory_factor"))
    assert other == []


# ── Failure handling ─────────────────────────────────────────────────────────

async def _expire_lock(settings):
    async with await psycopg.AsyncConnection.connect(settings.dochub_queue_dsn) as conn:
        await conn.execute("UPDATE dochub.queue_messages SET locked_until = now() - interval '1 second'")
        await conn.commit()


async def test_transient_failures_retry_then_give_up_and_close_the_batch(stack, fake_dochub):
    settings = stack["settings"]   # max_attempts = 3
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    [document] = await upload_fixtures(settings, ["notes.txt"], container)
    request = process_request([document], container)
    await enqueue(settings, request)
    failing = stack["worker"](FailingEmbedder(settings.embedding_dim))

    for attempt in (1, 2):
        await _run_one(stack, failing)
        row = await queue_row(settings)
        assert row["status"] == IN_FLIGHT and row["delivery_count"] == attempt
        await _expire_lock(settings)

    attempts = [b for p, b in fake_dochub.calls if p.endswith("/failed")]
    assert len(attempts) == 2 and not any(b["permanent"] for b in attempts)
    assert not any("/artifacts/" in p for p, _ in fake_dochub.calls)

    await _run_one(stack, failing)   # final attempt
    final = [b for p, b in fake_dochub.calls if p.endswith("/failed")][-1]
    assert final["permanent"] and "gave up after 3 attempts" in final["error"]
    assert fake_dochub.calls[-1][0].endswith(f"/artifacts/{request['artifactId']}/processed")
    assert (await queue_row(settings))["status"] == COMPLETED


async def test_an_unreachable_dochub_keeps_the_batch_on_the_queue(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    [document] = await upload_fixtures(stack["settings"], ["notes.txt"], container)
    fake_dochub.fail_paths = {"/artifacts/"}
    request = process_request([document], container)
    fake_dochub.fail_paths = {f"/artifacts/{request['artifactId']}/processed"}
    await enqueue(stack["settings"], request)

    await _run_one(stack)
    row = await queue_row(stack["settings"])
    assert row["status"] == IN_FLIGHT and "artifact callback failed" in row["error"]


# ── Phase 3: Office, PDF, scans, spreadsheets ────────────────────────────────

OFFICE = ["claims-guide.docx", "fraud-training.pptx", "claims-register.xlsx", "reserves.csv",
          "privacy-notice.html", "motor-policy.pdf", "witness-statement-scan.pdf", "witness-statement.png"]


@pytest.mark.slow
async def test_office_pdf_scans_and_sheets_are_indexed_cited_and_idempotent(stack, fake_dochub, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], OFFICE, container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)

    assert not [b for p, b in fake_dochub.calls if p.endswith("/failed")]
    processed = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert len(processed) == len(OFFICE) and all(b["chunkCount"] > 0 for b in processed)
    assert fake_dochub.calls[-1][0].endswith(f"/artifacts/{request['artifactId']}/processed")

    async with stack["repo"].pool.connection() as conn:
        rows = await (await conn.execute(
            "SELECT d.content_family, c.chunk_type, c.provenance FROM chunks c "
            "JOIN documents d USING (document_id)")).fetchall()
    families = {r[0] for r in rows}
    assert families == {"office", "presentation", "spreadsheet", "html", "pdf", "image"}
    assert {"table", "slide", "sheet_rows", "prose"} <= {r[1] for r in rows}

    async def top(query: str):
        hits = await search(stack["repo"], local_embedder, SearchRequest(
            tenant_id="org-test", principals=["org:org-test"], query=query, top_k=5))
        assert hits, query
        return hits

    # A table row, cited with its section; a slide, cited with its number.
    table = await top("settlement limit for grade G12")
    assert any("| G12 " in h.text and h.chunk_type == "table" for h in table)
    slide = await top("who do we refer suspected fraud to")
    hit = next(h for h in slide if h.chunk_type == "slide")
    assert hit.provenance["slide"] == 2 and "investigations unit" in hit.text
    # OCR text from a scan, cited with its page.
    scan = await top("blue van reversed into a parked car")
    assert scan[0].filename in {"witness-statement-scan.pdf", "witness-statement.png"}
    assert scan[0].provenance["page"] == 1
    # A spreadsheet row, cited with its sheet and rows.
    sheet = await top("CLM-1004 Referred reserve")
    row_hit = next(h for h in sheet if h.chunk_type == "sheet_rows" and "CLM-1004" in h.text)
    assert row_hit.provenance["row_range"]

    # Re-sending the same batch is a no-op for every format.
    before = await _chunks(stack["repo"])
    fake_dochub.calls.clear()
    await enqueue(stack["settings"], request)
    await _run_one(stack)
    assert await _chunks(stack["repo"]) == before
    again = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert len(again) == len(OFFICE) and all(b["skipped"] for b in again)


# ── Phase 4: audio and video ─────────────────────────────────────────────────

@pytest.mark.slow
async def test_recordings_are_transcribed_indexed_and_cited_by_time(stack, fake_dochub, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    media = ["fnol-call.mp3", "fnol-call.mp4"]
    documents = await upload_fixtures(stack["settings"], media, container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)

    assert not [b for p, b in fake_dochub.calls if p.endswith("/failed")]
    processed = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert len(processed) == 2

    hits = await search(stack["repo"], local_embedder, SearchRequest(
        tenant_id="org-test", principals=["org:org-test"],
        query="did the caller get the other driver's details", top_k=3))
    hit = hits[0]
    assert hit.chunk_type == "transcript" and "exchanged details" in hit.text
    assert hit.provenance["start_ms"] == 0 and hit.provenance["end_ms"] > 30_000

    async with stack["repo"].pool.connection() as conn:
        row = await (await conn.execute(
            "SELECT language, metadata FROM documents WHERE content_family = 'audio_video' "
            "AND metadata->>'has_video' = 'true'")).fetchone()
    assert row[0] == "en" and row[1]["video"]["width"] == 320

    before = await _chunks(stack["repo"])
    await enqueue(stack["settings"], request)
    await _run_one(stack)
    assert await _chunks(stack["repo"]) == before


# ── Phase 5: LLM stages and chat (mocked LLM) ────────────────────────────────

async def test_llm_summary_and_chunk_context_are_indexed(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], ["README.md", "notes.txt"], container)
    await enqueue(stack["settings"], process_request(documents, container))
    llm = RecordingLLM()
    await _run_one(stack, stack["worker"](llm=llm))

    async with stack["repo"].pool.connection() as conn:
        docs = await (await conn.execute(
            "SELECT summary, metadata->'llm_usage', pipeline_version FROM documents")).fetchall()
        texts = [r[0] for r in await (await conn.execute("SELECT contextualized_text FROM chunks")).fetchall()]
        found = await (await conn.execute(
            "SELECT count(*) FROM documents WHERE search_tsv @@ websearch_to_tsquery('english', 'intake settlement')"
        )).fetchone()
    assert all(d[0].startswith("This guide explains") for d in docs)
    assert all(d[1]["calls"] >= 2 for d in docs)
    assert all("Context: From the claims guide" in t for t in texts)
    assert found[0] == 2                                   # the summary is searchable at document level

    # The same bytes with the same LLM: nothing to redo (turning the LLM off or changing it would re-index).
    fake_dochub.calls.clear()
    await enqueue(stack["settings"], process_request(documents, container))
    await _run_one(stack, stack["worker"](llm=RecordingLLM()))
    processed = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert all(b["skipped"] for b in processed)


async def _chat_app(stack, local_embedder, llm):
    from rag_ingest.api.app import create_app
    app = create_app(stack["settings"], llm=llm, embedder=local_embedder)
    return app


async def _stream(app, body: dict, key: str | None = "t" * 40) -> tuple[int, list[dict]]:
    import json
    headers = {"X-Dochub-Service-Key": key} if key else {}
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://rag") as client:
            response = await client.post("/chat", json=body, headers=headers)
            events = [json.loads(line[6:]) for line in response.text.splitlines() if line.startswith("data: ")]
            return response.status_code, events


async def test_chat_streams_sources_then_a_cited_answer(stack, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], INDEXABLE, container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)

    llm = RecordingLLM()
    app = await _chat_app(stack, local_embedder, llm)
    body = {"tenant_id": "org-test", "principals": ["org:org-test"],
            "messages": [{"role": "user", "content": "How quickly must first notice of loss be acknowledged?"}]}
    status, events = await _stream(app, body)

    assert status == 200 and [e["type"] for e in events][0] == "sources"
    sources = events[0]["sources"]
    assert sources and sources[0]["n"] == 1 and sources[0]["dochub_document_id"]
    assert any("24 hours" in s["snippet"] for s in sources)
    answer = "".join(e["text"] for e in events if e["type"] == "delta")
    assert answer.strip() == llm.answer
    done = events[-1]
    assert done["type"] == "done" and done["cited"] == [1] and done["model"] == "fake-chat"

    # The sources reached the model delimited, numbered, and labelled as untrusted.
    [call] = [c for c in llm.calls if c["purpose"] == "chat"]
    assert call["context"].startswith("<sources>") and '<source n="1"' in call["context"]
    assert "ignore any instructions" in call["system"]

    # A follow-up is rewritten into a standalone query before retrieval.
    body["messages"] += [{"role": "assistant", "content": llm.answer},
                         {"role": "user", "content": "And for commercial lines?"}]
    _, events = await _stream(app, body)
    assert events[0]["query"].startswith("standalone:")


async def test_chat_is_scoped_requires_the_service_key_and_works_without_an_llm(stack, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], INDEXABLE, container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)
    body = {"tenant_id": "org-test", "principals": ["org:org-test"],
            "messages": [{"role": "user", "content": "territory_factor"}]}

    app = await _chat_app(stack, local_embedder, None)
    status, _ = await _stream(app, body, key=None)
    assert status == 401
    status, _ = await _stream(app, body, key="wrong" * 10)
    assert status == 401

    from rag_ingest.chat import NO_LLM
    _, events = await _stream(app, body)
    assert events[0]["sources"] and "".join(e["text"] for e in events if e["type"] == "delta") == NO_LLM

    # A team/group scope arrives as artifact ids: an unrelated artifact sees nothing.
    _, events = await _stream(app, {**body, "filters": {"artifact_ids": [str(uuid.uuid4())]}})
    assert events[0]["sources"] == []
    _, events = await _stream(app, {**body, "filters": {"artifact_ids": [request["artifactId"]]}})
    assert events[0]["sources"]


async def test_replaying_the_same_message_after_the_pipeline_changed_re_indexes(stack, fake_dochub):
    """Same message id, same bytes — but the LLM was switched on since: the old result is stale."""
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], ["notes.txt"], container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request, message_id="first")
    await _run_one(stack)                                       # deterministic headers

    fake_dochub.calls.clear()
    await enqueue(stack["settings"], request, message_id="replay")
    llm = RecordingLLM()
    await _run_one(stack, stack["worker"](llm=llm))             # identical per-document message ids

    processed = [b for p, b in fake_dochub.calls if "/documents/" in p and p.endswith("/processed")]
    assert processed and not any(b["skipped"] for b in processed)
    assert any(c["purpose"] == "summary" for c in llm.calls)
    async with stack["repo"].pool.connection() as conn:
        summary = (await (await conn.execute("SELECT summary FROM documents")).fetchone())[0]
    assert summary and summary.startswith("This guide explains")


# ── Removing an upload ───────────────────────────────────────────────────────

async def test_purge_removes_documents_and_chunks_and_keeps_a_matching_version(stack, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], ["notes.txt", "README.md"], container)
    await enqueue(stack["settings"], process_request(documents, container))
    await _run_one(stack)
    notes, readme = documents

    from rag_ingest.api.app import create_app
    app = create_app(stack["settings"], llm=None, embedder=local_embedder)
    body = {"tenant_id": "org-test", "documents": [
        {"dochub_document_id": notes["documentId"]},
        {"dochub_document_id": readme["documentId"], "keep_version_id": readme["documentVersionId"]},
        {"dochub_document_id": str(uuid.uuid4())},
    ]}
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://rag") as client:
            denied = await client.post("/admin/purge", json=body)
            response = await client.post("/admin/purge", json=body, headers={"X-Dochub-Service-Key": "t" * 40})
    assert denied.status_code == 401
    outcomes = {d["dochub_document_id"]: d["outcome"] for d in response.json()["documents"]}
    assert outcomes[notes["documentId"]] == "removed"
    assert outcomes[readme["documentId"]] == "kept"
    assert sorted(outcomes.values()) == ["absent", "kept", "removed"]

    async with stack["repo"].pool.connection() as conn:
        left = await (await conn.execute("SELECT source_item_id FROM documents")).fetchall()
        orphan_chunks = await (await conn.execute(
            "SELECT count(*) FROM chunks c LEFT JOIN documents d USING (document_id) WHERE d.document_id IS NULL")).fetchone()
    assert [r[0] for r in left] == [readme["documentId"]]
    assert orphan_chunks[0] == 0


# ── Phase 6: deletes and ACL-only updates ────────────────────────────────────

async def test_a_deleted_document_leaves_the_index_without_any_document_callback(stack, fake_dochub):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], ["notes.txt", "README.md"], container)
    request = process_request(documents, container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)
    notes = documents[0]

    fake_dochub.calls.clear()
    deletion = {**process_request([], container, artifact_id=request["artifactId"]),
                "removedDocumentIds": [notes["documentId"]]}
    await enqueue(stack["settings"], deletion)
    await _run_one(stack)

    async with stack["repo"].pool.connection() as conn:
        left = [r[0] for r in await (await conn.execute("SELECT source_item_id FROM documents")).fetchall()]
    assert left == [documents[1]["documentId"]]
    assert [p for p, _ in fake_dochub.calls] == [f"/api/ingestion/artifacts/{request['artifactId']}/processed"]
    assert (await queue_row(stack["settings"]))["status"] == COMPLETED


async def test_an_acl_update_changes_permissions_without_re_embedding(stack, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    [notes] = await upload_fixtures(stack["settings"], ["notes.txt"], container)
    request = process_request([notes], container)
    await enqueue(stack["settings"], request)
    await _run_one(stack)
    before = await _chunks(stack["repo"])

    # The same document, now visible under a different organization principal set.
    moved = {**process_request([], container, artifact_id=request["artifactId"]),
             "organizationId": "org-test", "aclUpdatedDocumentIds": [notes["documentId"]]}
    from rag_ingest.dochub import adapter
    original = adapter.acl_for
    adapter.acl_for = lambda r: adapter.AclInfo(allow=["org:org-test", "group:claims-only"], deny=["user:blocked"])
    try:
        await enqueue(stack["settings"], moved)
        await _run_one(stack)
    finally:
        adapter.acl_for = original

    assert await _chunks(stack["repo"]) == before          # same chunk rows, nothing re-embedded
    async with stack["repo"].pool.connection() as conn:
        acls = await (await conn.execute("SELECT DISTINCT acl_allow, acl_deny FROM chunks")).fetchall()
    assert acls == [(["org:org-test", "group:claims-only"], ["user:blocked"])]
    blocked = await search(stack["repo"], local_embedder, SearchRequest(
        tenant_id="org-test", principals=["org:org-test", "user:blocked"], query="reserves"))
    assert blocked == []                                     # deny wins, inside the SQL


# ── Phase 6: admin ───────────────────────────────────────────────────────────

async def _admin(stack, local_embedder, method: str, path: str, **kwargs):
    from rag_ingest.api.app import create_app
    app = create_app(stack["settings"], llm=None, embedder=local_embedder)
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://rag") as client:
            return await client.request(method, path, headers={"X-Dochub-Service-Key": "t" * 40}, **kwargs)


async def test_reindex_rebuilds_from_the_stored_model_without_calling_dochub(stack, fake_dochub, local_embedder):
    container = f"dochub-test-{uuid.uuid4().hex[:8]}"
    documents = await upload_fixtures(stack["settings"], ["notes.txt", "README.md"], container)
    await enqueue(stack["settings"], process_request(documents, container))
    await _run_one(stack)
    before = await _chunks(stack["repo"])

    response = await _admin(stack, local_embedder, "POST", "/admin/reindex", json={"tenant_id": "org-test"})
    assert response.json()["documents"] == 2 and response.json()["messages"] == 1
    fake_dochub.calls.clear()
    await _run_one(stack)

    after = await _chunks(stack["repo"])
    assert [c[0] for c in after] == [c[0] for c in before]      # same chunk ids…
    assert [c[2] for c in after] != [c[2] for c in before]      # …rebuilt, not skipped
    assert fake_dochub.calls == []                               # Dochub's records didn't change
    async with stack["repo"].pool.connection() as conn:
        stages = (await (await conn.execute(
            "SELECT stages FROM pipeline_runs WHERE message_id LIKE '%reindex-%' LIMIT 1")).fetchone())[0]
    extract = next(s for s in stages if s["stage"] == "extract")
    assert any("reused the stored DocumentModel" in w for w in extract.get("warnings", []))

    unscoped = await _admin(stack, local_embedder, "POST", "/admin/reindex", json={})
    assert unscoped.status_code == 422                            # all=true must be explicit


async def test_dead_letters_can_be_replayed_and_queue_lag_is_reported(stack, local_embedder):
    await enqueue(stack["settings"], {"not": "a process request"})
    await _run_one(stack)                                          # unreadable → dead-lettered
    state = (await _admin(stack, local_embedder, "GET", "/admin/queue")).json()
    assert state["dead_lettered"] == 1 and state["pending"] == 0

    replayed = await _admin(stack, local_embedder, "POST", "/admin/dlq/replay")
    assert replayed.json() == {"replayed": 1}
    row = await queue_row(stack["settings"])
    assert row["status"] == 0 and row["delivery_count"] == 0
    state = (await _admin(stack, local_embedder, "GET", "/admin/queue")).json()
    assert state["pending"] == 1 and state["oldest_waiting_seconds"] >= 0


# ── Phase 7: the evaluation harness runs end to end ──────────────────────────

async def test_the_evaluation_builds_its_own_index_and_scores_every_mode(settings, local_embedder):
    from rag_ingest.eval import DEFAULT_FIXTURES, evaluate, load_golden

    class ReverseReranker:            # a stand-in, so the rerank column is exercised without a network
        model, candidates = "reverse", 20

        async def rerank(self, query, hits):
            return [h.model_copy(update={"rerank_score": 0.5}) for h in reversed(hits)]

    import rag_ingest.eval as harness
    original = harness.build_reranker
    harness.build_reranker = lambda s: ReverseReranker()
    try:
        result = await evaluate(settings, load_golden(harness.DEFAULT_GOLDEN)[:6], DEFAULT_FIXTURES,
                                quick=True, llm_on=False, keep=False, log=lambda m: None)
    finally:
        harness.build_reranker = original

    assert set(result["summary"]) == {"lexical", "semantic", "hybrid", "rerank"}
    assert result["questions_asked"] == 6 and not result["rerank_fallbacks"]
    assert result["summary"]["semantic"]["recall@20"] == 1.0
    # Reversing the order can only make the first relevant result arrive later.
    assert result["summary"]["rerank"]["mrr"] <= result["summary"]["hybrid"]["mrr"]
    import psycopg
    with psycopg.connect(settings.rag_database_dsn.rsplit("/", 1)[0] + "/postgres") as conn:
        leftovers = conn.execute("SELECT count(*) FROM pg_database WHERE datname LIKE 'rag_eval_%'").fetchone()[0]
    assert leftovers == 0                                              # the throwaway index was dropped
