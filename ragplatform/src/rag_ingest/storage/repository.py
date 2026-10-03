"""All SQL the ingestion side runs against its own database."""

from __future__ import annotations

import json
import uuid
from datetime import datetime, timezone
from typing import Any

import numpy as np
from pgvector.psycopg import register_vector_async
from psycopg import AsyncConnection
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool

from rag_ingest.config import Settings
from rag_ingest.models import Chunk


async def _configure(conn: AsyncConnection) -> None:
    await register_vector_async(conn)
    # The registration queries pg_type, which opens a transaction. The pool
    # discards any connection a configure hook leaves non-idle, so close it.
    await conn.commit()


class Repository:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._pool = AsyncConnectionPool(
            settings.rag_database_dsn,
            min_size=settings.db_pool_min,
            max_size=settings.db_pool_max,
            configure=_configure,
            open=False,
        )

    async def open(self) -> None:
        if self._pool.closed:
            await self._pool.open()

    async def close(self) -> None:
        if not self._pool.closed:
            await self._pool.close()

    async def ping(self) -> bool:
        async with self._pool.connection() as conn:
            await conn.execute("SELECT 1")
        return True

    # ── Runs (idempotency + lineage) ───────────────────────────────────────────

    async def succeeded_run(self, message_id: str) -> dict[str, Any] | None:
        async with self._pool.connection() as conn:
            cur = conn.cursor(row_factory=dict_row)
            await cur.execute(
                "SELECT * FROM pipeline_runs WHERE message_id = %s AND status IN ('succeeded', 'skipped')",
                (message_id,),
            )
            return await cur.fetchone()

    async def start_run(self, document_id: str, message_id: str, content_hash: str, attempt: int,
                        pipeline_version: str) -> uuid.UUID:
        """One row per message: a redelivery reuses it and records the new attempt."""
        run_id = uuid.uuid4()
        async with self._pool.connection() as conn:
            row = await (await conn.execute(
                """
                INSERT INTO pipeline_runs (run_id, document_id, message_id, content_hash,
                                           pipeline_version, status, attempt)
                VALUES (%s, %s, %s, %s, %s, 'running', %s)
                ON CONFLICT (message_id) DO UPDATE
                   SET status = 'running', attempt = EXCLUDED.attempt, started_at = now(),
                       finished_at = NULL, error = NULL, failed_stage = NULL, permanent = NULL,
                       content_hash = EXCLUDED.content_hash, stages = '[]'
                RETURNING run_id
                """,
                (run_id, document_id, message_id, content_hash, pipeline_version, attempt),
            )).fetchone()
        return row[0]

    async def finish_run(
        self, run_id: uuid.UUID, status: str, stages: list[dict[str, Any]],
        error: str | None = None, failed_stage: str | None = None, permanent: bool | None = None,
        content_hash: str | None = None,
    ) -> None:
        async with self._pool.connection() as conn:
            await conn.execute(
                """
                UPDATE pipeline_runs
                   SET status = %s, stages = %s, error = %s, failed_stage = %s, permanent = %s,
                       content_hash = COALESCE(%s, content_hash), finished_at = now()
                 WHERE run_id = %s
                """,
                (status, json.dumps(stages), error, failed_stage, permanent, content_hash, run_id),
            )

    async def runs_for_document(self, document_id: str, limit: int = 20) -> list[dict[str, Any]]:
        async with self._pool.connection() as conn:
            cur = conn.cursor(row_factory=dict_row)
            await cur.execute(
                "SELECT * FROM pipeline_runs WHERE document_id = %s ORDER BY started_at DESC LIMIT %s",
                (document_id, limit),
            )
            return await cur.fetchall()

    # ── Documents ──────────────────────────────────────────────────────────────

    async def purge(self, tenant_id: str, items: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """
        Removes Dochub documents from the index, in one transaction. Each item names
        a Dochub document; with `keep_version_id`, a document whose index still holds
        exactly that version is kept (and its failure cleared) instead of removed.
        Chunks and runs go with their document (ON DELETE CASCADE).
        """
        outcomes: list[dict[str, Any]] = []
        async with self._pool.connection() as conn, conn.transaction():
            cur = conn.cursor(row_factory=dict_row)
            for item in items:
                await cur.execute(
                    "SELECT document_id, document_version_id, status FROM documents "
                    "WHERE tenant_id = %s AND source_item_id = %s",
                    (tenant_id, item["dochub_document_id"]))
                rows = await cur.fetchall()
                if not rows:
                    outcomes.append({"dochub_document_id": item["dochub_document_id"], "outcome": "absent", "document_ids": []})
                    continue
                keep = item.get("keep_version_id")
                if keep and all(r["document_version_id"] == keep for r in rows):
                    await cur.execute(
                        "UPDATE documents SET status = 'indexed', error = NULL, updated_at = now() "
                        "WHERE tenant_id = %s AND source_item_id = %s AND status = 'failed' "
                        "AND EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = documents.document_id)",
                        (tenant_id, item["dochub_document_id"]))
                    outcomes.append({"dochub_document_id": item["dochub_document_id"], "outcome": "kept",
                                     "document_ids": [r["document_id"] for r in rows]})
                    continue
                ids = [r["document_id"] for r in rows]
                await cur.execute("DELETE FROM documents WHERE document_id = ANY(%s)", (ids,))
                outcomes.append({"dochub_document_id": item["dochub_document_id"], "outcome": "removed",
                                 "document_ids": ids})
        return outcomes

    async def update_acl(self, tenant_id: str, dochub_document_id: str, allow: list[str], deny: list[str]) -> int:
        """Rewrites a document's permissions on it and every chunk; no re-embedding. Returns chunks touched."""
        async with self._pool.connection() as conn, conn.transaction():
            await conn.execute(
                "UPDATE documents SET acl_allow = %s, acl_deny = %s, updated_at = now() "
                "WHERE tenant_id = %s AND source_item_id = %s", (allow, deny, tenant_id, dochub_document_id))
            cur = await conn.execute(
                "UPDATE chunks SET acl_allow = %s, acl_deny = %s WHERE document_id IN "
                "(SELECT document_id FROM documents WHERE tenant_id = %s AND source_item_id = %s)",
                (allow, deny, tenant_id, dochub_document_id))
            return cur.rowcount

    async def get_document(self, document_id: str) -> dict[str, Any] | None:
        async with self._pool.connection() as conn:
            cur = conn.cursor(row_factory=dict_row)
            await cur.execute("SELECT * FROM documents WHERE document_id = %s", (document_id,))
            return await cur.fetchone()

    async def chunk_count(self, document_id: str) -> int:
        async with self._pool.connection() as conn:
            row = await (await conn.execute(
                "SELECT count(*) FROM chunks WHERE document_id = %s", (document_id,)
            )).fetchone()
        return int(row[0])

    async def touch_document(self, document_id: str, fields: dict[str, Any]) -> None:
        """Metadata-only update for a skipped re-run: no chunks, no embeddings."""
        allowed = {"source_version", "artifact_id", "source_document_id", "document_version_id",
                   "source_uri", "metadata", "acl_allow", "acl_deny"}
        sets = {k: v for k, v in fields.items() if k in allowed}
        if not sets:
            return
        assignments = ", ".join(f"{k} = %s" for k in sets) + ", updated_at = now()"
        values = [json.dumps(v) if k == "metadata" else v for k, v in sets.items()]
        async with self._pool.connection() as conn, conn.transaction():
            await conn.execute(f"UPDATE documents SET {assignments} WHERE document_id = %s",
                               (*values, document_id))
            if "acl_allow" in sets or "acl_deny" in sets:
                await conn.execute(
                    "UPDATE chunks SET acl_allow = d.acl_allow, acl_deny = d.acl_deny "
                    "FROM documents d WHERE chunks.document_id = d.document_id AND d.document_id = %s",
                    (document_id,),
                )

    async def mark_failed(self, document: dict[str, Any], error: str) -> None:
        """Records a failure on the document row without touching its existing chunks."""
        async with self._pool.connection() as conn:
            await conn.execute(
                """
                INSERT INTO documents (document_id, tenant_id, source, source_item_id, source_uri,
                    source_version, content_hash, content_type, content_family, raw_object_key,
                    pipeline_version, status, error, artifact_id, source_document_id,
                    document_version_id, metadata)
                VALUES (%(document_id)s, %(tenant_id)s, %(source)s, %(source_item_id)s, %(source_uri)s,
                    %(source_version)s, %(content_hash)s, %(content_type)s, %(content_family)s,
                    %(raw_object_key)s, %(pipeline_version)s, 'failed', %(error)s, %(artifact_id)s,
                    %(source_document_id)s, %(document_version_id)s, %(metadata)s)
                ON CONFLICT (document_id) DO UPDATE
                   SET status = 'failed', error = EXCLUDED.error, updated_at = now()
                """,
                {**document, "error": error[:4000], "metadata": json.dumps(document.get("metadata", {}))},
            )

    # ── Atomic index (stage 9) ─────────────────────────────────────────────────

    async def index(self, document: dict[str, Any], chunks: list[Chunk], chunker_name: str,
                    chunker_version: str, ts_configs: dict[str, str]) -> None:
        """
        Upsert the document, drop its old chunks and insert the new ones in ONE
        transaction, so a search never sees a half-indexed document.
        """
        dim = self._settings.embedding_dim
        now = datetime.now(timezone.utc)
        rows = []
        for chunk in chunks:
            if chunk.embedding is None or len(chunk.embedding) != dim:
                raise ValueError(f"chunk {chunk.chunk_id} has no {dim}-dim embedding")
            rows.append((
                chunk.chunk_id, chunk.document_id, chunk.tenant_id, document.get("artifact_id"),
                chunk.ordinal, chunk.text, chunk.contextualized_text, chunk.token_count, chunk.chunk_type,
                chunk.heading_path, chunk.provenance.model_dump_json(), json.dumps(chunk.metadata),
                document["acl_allow"], document["acl_deny"], document["content_family"],
                chunker_name, chunker_version, document["pipeline_version"],
                self._settings.embedding_model, dim, np.asarray(chunk.embedding, dtype=np.float32),
                ts_configs.get(chunk.chunk_type, ts_configs.get("*", "english")),
            ))

        async with self._pool.connection() as conn, conn.transaction():
            await conn.execute(
                """
                INSERT INTO documents (document_id, tenant_id, source, source_item_id, source_uri,
                    source_version, content_hash, content_type, content_family, title, language, summary,
                    metadata, acl_allow, acl_deny, raw_object_key, model_object_key, pipeline_version,
                    embedding_model, status, error, artifact_id, source_document_id, document_version_id,
                    search_tsv, updated_at, deleted_at)
                VALUES (%(document_id)s, %(tenant_id)s, %(source)s, %(source_item_id)s, %(source_uri)s,
                    %(source_version)s, %(content_hash)s, %(content_type)s, %(content_family)s, %(title)s,
                    %(language)s, %(summary)s, %(metadata)s, %(acl_allow)s, %(acl_deny)s,
                    %(raw_object_key)s, %(model_object_key)s, %(pipeline_version)s, %(embedding_model)s,
                    'indexed', NULL, %(artifact_id)s, %(source_document_id)s, %(document_version_id)s,
                    to_tsvector('english', coalesce(%(title)s, '') || ' ' || coalesce(%(summary)s, '')
                                         || ' ' || coalesce(%(filename)s, '')),
                    %(now)s, NULL)
                ON CONFLICT (document_id) DO UPDATE SET
                    source_uri = EXCLUDED.source_uri, source_version = EXCLUDED.source_version,
                    content_hash = EXCLUDED.content_hash, content_type = EXCLUDED.content_type,
                    content_family = EXCLUDED.content_family, title = EXCLUDED.title,
                    language = EXCLUDED.language, summary = EXCLUDED.summary, metadata = EXCLUDED.metadata,
                    acl_allow = EXCLUDED.acl_allow, acl_deny = EXCLUDED.acl_deny,
                    raw_object_key = EXCLUDED.raw_object_key, model_object_key = EXCLUDED.model_object_key,
                    pipeline_version = EXCLUDED.pipeline_version, embedding_model = EXCLUDED.embedding_model,
                    status = 'indexed', error = NULL, artifact_id = EXCLUDED.artifact_id,
                    source_document_id = EXCLUDED.source_document_id,
                    document_version_id = EXCLUDED.document_version_id,
                    search_tsv = EXCLUDED.search_tsv, updated_at = EXCLUDED.updated_at, deleted_at = NULL
                """,
                {**document, "metadata": json.dumps(document.get("metadata", {})), "now": now},
            )
            await conn.execute("DELETE FROM chunks WHERE document_id = %s", (document["document_id"],))
            async with conn.cursor() as cur:
                await cur.executemany(
                    """
                    INSERT INTO chunks (chunk_id, document_id, tenant_id, artifact_id, ordinal, text,
                        contextualized_text, token_count, chunk_type, heading_path, provenance, metadata,
                        acl_allow, acl_deny, content_family, chunker_name, chunker_version,
                        pipeline_version, embedding_model, embedding_dim, embedding, ts_config)
                    VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s,
                            %s, %s, %s, %s::regconfig)
                    """,
                    rows,
                )

    async def connection(self):
        return self._pool.connection()

    @property
    def pool(self) -> AsyncConnectionPool:
        return self._pool
