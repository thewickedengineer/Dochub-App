"""Stage 6 — chunk with the strategy for this content family."""

from __future__ import annotations

import asyncio

from rag_ingest.chunkers.registry import ChunkerRegistry
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError


class ChunkStage:
    name, version = "chunk", "1"

    def __init__(self, registry: ChunkerRegistry) -> None:
        self.registry = registry

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        assert ctx.model is not None and ctx.family is not None
        chunker = self.registry.for_family(ctx.family)
        ctx.chunks = await asyncio.to_thread(chunker.chunk, ctx.model, ctx.message.tenant_id)
        ctx.chunker_name, ctx.chunker_version = chunker.name, chunker.version
        if not ctx.chunks:
            raise PermanentError("no_chunks", "extraction produced text but no chunks")
        return ctx
