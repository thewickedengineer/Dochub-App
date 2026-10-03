"""Stage 9 — write the document and swap its chunks atomically."""

from __future__ import annotations

from rag_ingest.observability import CHUNKS_WRITTEN
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import TransientError
from rag_ingest.storage.repository import Repository


class IndexStage:
    name, version = "index", "1"

    def __init__(self, repo: Repository, embedding_model: str, versions) -> None:
        self.repo = repo
        self.embedding_model = embedding_model
        self.versions = versions

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        assert ctx.family is not None
        ctx.pipeline_version = self.versions.for_family(ctx.family)
        if ctx.llm_usage.calls and ctx.model is not None:
            # What this version of the document cost to enrich, next to the document itself.
            ctx.model.metadata["llm_usage"] = ctx.llm_usage.as_dict()
        row = ctx.document_row()
        row.update({"pipeline_version": ctx.pipeline_version, "embedding_model": self.embedding_model})
        try:
            await self.repo.index(row, ctx.chunks, ctx.chunker_name, ctx.chunker_version, ctx.ts_configs)
        except Exception as error:  # noqa: BLE001 — the transaction rolled back; nothing half-written
            raise TransientError("index_failed", f"{type(error).__name__}: {error}") from error
        CHUNKS_WRITTEN.inc(len(ctx.chunks))
        return ctx
