"""
Health, metrics, search, chat and run inspection.

Everything except /health and /metrics requires Dochub's service key: search
and chat take the tenant and principals from the request, so only Dochub's API
— which derives them from the signed-in user — may call them.
"""

from __future__ import annotations

import hmac
import json
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, HTTPException, Response
from pydantic import BaseModel, Field
from fastapi.responses import StreamingResponse
from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

from rag_ingest.admin import ReindexRequest, reindex
from rag_ingest.chat import ChatRequest, answer
from rag_ingest.config import Settings, get_settings
from rag_ingest.llm.client import build_llm
from rag_ingest.llm.embedder import build_embedder
from rag_ingest.pipeline.errors import StageError
from rag_ingest.queue import build_queue
from rag_ingest.search import SearchHit, SearchRequest, build_reranker, search
from rag_ingest.storage.blob import BlobStore
from rag_ingest.storage.migrate import migrate
from rag_ingest.storage.repository import Repository

SERVICE_KEY_HEADER = "X-Dochub-Service-Key"


class PurgeItem(BaseModel):
    dochub_document_id: str
    # Keep the indexed copy when it is exactly this version (a rolled-back update).
    keep_version_id: str | None = None


class PurgeRequest(BaseModel):
    tenant_id: str
    documents: list[PurgeItem] = Field(min_length=1, max_length=5000)


def create_app(settings: Settings | None = None, *, llm=None, embedder=None, reranker=None) -> FastAPI:
    settings = settings or get_settings()
    state: dict = {}

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        migrate(settings)
        repo = Repository(settings)
        await repo.open()
        state["repo"] = repo
        state["embedder"] = embedder or build_embedder(settings)
        state["llm"] = llm if llm is not None else build_llm(settings)
        state["blobs"] = BlobStore(settings)
        state["reranker"] = reranker if reranker is not None else build_reranker(settings)
        state["queue"] = None   # opened on first admin use; search and chat never need it
        yield
        if state["queue"] is not None:
            await state["queue"].close()
        if hasattr(state["reranker"], "close"):
            await state["reranker"].close()
        await state["blobs"].close()
        await repo.close()

    def require_service_key(key: str | None = Header(default=None, alias=SERVICE_KEY_HEADER)) -> None:
        expected = settings.dochub_service_key.get_secret_value()
        if len(expected) < 32:
            raise HTTPException(503, "DOCHUB_SERVICE_KEY is not configured")
        if not key or not hmac.compare_digest(key.encode(), expected.encode()):
            raise HTTPException(401, f"missing or wrong {SERVICE_KEY_HEADER}")

    app = FastAPI(title="Dochub RAG platform", version=settings.pipeline_version, lifespan=lifespan)
    secured = [Depends(require_service_key)]

    @app.middleware("http")
    async def traced(request, call_next):
        from rag_ingest.observability import tracer
        with tracer.start_as_current_span(f"{request.method} {request.url.path}", attributes={
                "http.request.method": request.method, "url.path": request.url.path}) as span:
            response = await call_next(request)
            span.set_attribute("http.response.status_code", response.status_code)
            return response

    @app.get("/health")
    async def health() -> dict:
        try:
            await state["repo"].ping()
        except Exception as error:  # noqa: BLE001
            raise HTTPException(503, f"database unavailable: {error}") from error
        llm_client = state["llm"]
        return {"status": "ok", "pipeline_version": settings.pipeline_version,
                "embedding_model": state["embedder"].model, "embedding_dim": state["embedder"].dim,
                "llm": {"provider": llm_client.provider, "model": llm_client.model,
                        "chat_model": llm_client.chat_model} if llm_client else None,
                "reranker": getattr(state["reranker"], "model", None)}

    @app.get("/metrics")
    async def metrics() -> Response:
        return Response(generate_latest(), media_type=CONTENT_TYPE_LATEST)

    @app.post("/search", response_model=list[SearchHit], dependencies=secured)
    async def run_search(request: SearchRequest) -> list[SearchHit]:
        return await search(state["repo"], state["embedder"], request, reranker=state["reranker"],
                            lexical_match=settings.lexical_match, lexical_weight=settings.lexical_weight)

    @app.post("/chat", dependencies=secured)
    async def chat(request: ChatRequest) -> StreamingResponse:
        if request.messages[-1].role != "user":
            raise HTTPException(422, "the last message must be the user's")

        async def events():
            try:
                async for event in answer(state["repo"], state["embedder"], state["llm"], settings, request,
                                          reranker=state["reranker"]):
                    yield f"data: {json.dumps(event)}\n\n"
            except StageError as error:
                # Headers are already sent: report the failure in-band, then end the stream.
                yield f"data: {json.dumps({'type': 'error', 'error': error.reason, 'detail': error.detail[:300]})}\n\n"

        return StreamingResponse(events(), media_type="text/event-stream",
                                 headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})

    @app.post("/admin/purge", dependencies=secured)
    async def purge(request: PurgeRequest) -> dict:
        """
        Dochub removing an upload: its documents leave the index (chunks, runs and
        stored DocumentModels with them). Idempotent — a document already gone is 'absent'.
        """
        outcomes = await state["repo"].purge(request.tenant_id, [i.model_dump() for i in request.documents])
        for outcome in outcomes:
            if outcome["outcome"] == "removed":
                for document_id in outcome["document_ids"]:
                    try:
                        await state["blobs"].delete_prefix(f"models/{request.tenant_id}/{document_id}/")
                    except Exception:  # noqa: BLE001 — the index is already clean; a stray model file is harmless
                        pass
        return {"documents": [{k: v for k, v in o.items() if k != "document_ids"} for o in outcomes]}

    def queue():
        if state["queue"] is None:
            state["queue"] = build_queue(settings)
        return state["queue"]

    @app.post("/admin/reindex", dependencies=secured)
    async def run_reindex(request: ReindexRequest) -> dict:
        """Rebuild documents (some, a tenant's, or everything) through the queue."""
        return await reindex(state["repo"], queue(), request)

    @app.post("/admin/dlq/replay", dependencies=secured)
    async def replay(limit: int = 100) -> dict:
        """Put dead-lettered process messages back on the queue with fresh attempts."""
        return {"replayed": await queue().replay_dead_letters(max(1, min(limit, 1000)))}

    @app.get("/admin/queue", dependencies=secured)
    async def queue_state() -> dict:
        return {"queue": settings.process_queue, "backend": settings.queue_backend, **await queue().depth()}

    @app.get("/admin/runs/{document_id}", dependencies=secured)
    async def runs(document_id: str) -> dict:
        document = await state["repo"].get_document(document_id)
        if document is None:
            raise HTTPException(404, "no such document")
        document.pop("search_tsv", None)
        return {"document": document, "runs": await state["repo"].runs_for_document(document_id)}

    return app
