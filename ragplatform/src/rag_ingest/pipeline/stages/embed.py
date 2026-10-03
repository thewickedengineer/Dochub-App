"""Stage 8 — embed contextualized text, batched by token budget."""

from __future__ import annotations

import asyncio

from rag_ingest.config import Settings
from rag_ingest.llm.embedder import Embedder
from rag_ingest.observability import TOKENS_EMBEDDED
from rag_ingest.pipeline.context import PipelineContext


class EmbedStage:
    name, version = "embed", "1"

    def __init__(self, settings: Settings, embedder: Embedder, semaphore: asyncio.Semaphore) -> None:
        self.settings = settings
        self.embedder = embedder
        self.semaphore = semaphore

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        tok = self.embedder.tokenizer
        batches: list[list[int]] = [[]]
        used = 0
        for index, chunk in enumerate(ctx.chunks):
            n = tok.count(chunk.contextualized_text)
            if batches[-1] and used + n > self.settings.embedding_batch_tokens:
                batches.append([])
                used = 0
            batches[-1].append(index)
            used += n

        async def run_batch(indices: list[int]) -> None:
            texts = [ctx.chunks[i].contextualized_text for i in indices]
            async with self.semaphore:
                vectors = await self.embedder.embed(texts)
            for i, vector in zip(indices, vectors):
                ctx.chunks[i].embedding = vector
            TOKENS_EMBEDDED.labels(self.embedder.model).inc(sum(tok.count(t) for t in texts))

        await asyncio.gather(*(run_batch(b) for b in batches if b))
        return ctx
