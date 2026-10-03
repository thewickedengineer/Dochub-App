"""
Stage 7 — situate each chunk in its document before embedding.

Every chunk gets a deterministic header: document, location in Dochub, where in
the document (section, page, slide, sheet rows, time range), and for code the
file, language, symbol and imports. With an LLM configured, every non-code chunk
also gets a one- or two-sentence context written by the LLM from the whole
document (spec 2.4), with the document sent as a cached prompt prefix so each
chunk only pays for itself. Code keeps the deterministic header, which the spec
specifies for it. Both the embedding and the full-text index read
`contextualized_text`.
"""

from __future__ import annotations

import asyncio

from rag_ingest.config import Settings
from rag_ingest.llm.client import LLMClient
from rag_ingest.llm.prompts import CONTEXT_SYSTEM, PROMPT_VERSION, context_prompt, document_text
from rag_ingest.llm.tokenizer import Tokenizer, truncate
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import StageError


class ContextualizeStage:
    name = "contextualize"

    def __init__(self, tokenizer: Tokenizer, model_max_tokens: int, settings: Settings | None = None,
                 llm: LLMClient | None = None, slots: asyncio.Semaphore | None = None) -> None:
        self.tokenizer = tokenizer
        self.model_max = model_max_tokens
        self.settings = settings
        self.llm = llm if settings is not None and settings.context_enabled else None
        self.slots = slots or asyncio.Semaphore(settings.llm_concurrency if settings else 4)
        self.version = f"3-llm-p{PROMPT_VERSION}" if self.llm else "2-deterministic"

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        model = ctx.model
        assert model is not None
        where = " › ".join(p for p in (ctx.hint("team_name"), ctx.hint("group_name"), ctx.hint("artifact_name")) if p)
        contexts = await self._llm_contexts(ctx)

        for chunk in ctx.chunks:
            lines = [f"Document: {model.title}"] if model.title else []
            if where:
                lines.append(f"In: {where}")

            if chunk.chunk_type == "code_symbol":
                meta = chunk.metadata
                lines.append(f"File: {meta.get('file_path')} ({meta.get('language') or 'code'})")
                if meta.get("class_signature"):
                    lines.append(f"Class: {meta['class_signature'].strip()}")
                    if meta.get("class_docstring"):
                        lines.append(f"Class docstring: {meta['class_docstring']}")
                if meta.get("symbol_name"):
                    lines.append(f"Symbol: {meta.get('symbol_kind', '').replace('_', ' ')} {meta['symbol_name']}")
                if meta.get("imports"):
                    lines.append(f"Imports:\n{meta['imports']}")
            elif chunk.heading_path and chunk.chunk_type != "sheet_rows":
                lines.append("Section: " + " › ".join(chunk.heading_path))
            located = _location(chunk)
            if located:
                lines.append(located)
            if contexts.get(chunk.chunk_id):
                lines.append(f"Context: {contexts[chunk.chunk_id]}")

            header = "\n".join(lines)
            # Header plus chunk must fit what the model will read; the chunk wins.
            room = self.model_max - chunk.token_count - 4
            header = truncate(self.tokenizer, header, max(0, room))
            chunk.contextualized_text = f"{header}\n\n{chunk.text}" if header else chunk.text
            if contexts.get(chunk.chunk_id):
                chunk.metadata["context"] = contexts[chunk.chunk_id]
        return ctx

    async def _llm_contexts(self, ctx: PipelineContext) -> dict[str, str]:
        if self.llm is None:
            return {}
        targets = [c for c in ctx.chunks if c.chunk_type != "code_symbol"]
        if not targets:
            return {}
        if len(targets) > self.settings.context_max_chunks:
            ctx.warnings.append(f"{len(targets)} chunks exceed CONTEXT_MAX_CHUNKS="
                                f"{self.settings.context_max_chunks}; kept deterministic headers")
            return {}
        # One document text for every call: identical bytes are what the prompt cache matches.
        document, truncated = document_text(ctx.model, self.tokenizer, self.settings.llm_max_document_tokens,
                                            summary=ctx.summary)
        if truncated:
            ctx.warnings.append(f"chunk context written from the first {self.settings.llm_max_document_tokens} tokens")

        async def one(chunk):
            async with self.slots:
                result = await self.llm.complete(
                    purpose="context", system=CONTEXT_SYSTEM, prompt=context_prompt(chunk.text),
                    document=document, cache_key=ctx.document_id, max_tokens=self.settings.context_max_tokens)
            ctx.llm_usage.add(result.usage)
            return chunk.chunk_id, " ".join(result.text.split())

        # The first call writes the cache; the rest read it. A TaskGroup cancels
        # the remaining calls as soon as one fails, so a dead endpoint costs one error.
        first = await one(targets[0])
        try:
            async with asyncio.TaskGroup() as group:
                tasks = [group.create_task(one(c)) for c in targets[1:]]
        except* StageError as failures:
            raise failures.exceptions[0] from None
        return dict([first, *(task.result() for task in tasks)])


def _clock(ms: int) -> str:
    seconds = ms // 1000
    hours, rest = divmod(seconds, 3600)
    return f"{hours}:{rest // 60:02d}:{rest % 60:02d}" if hours else f"{rest // 60:02d}:{rest % 60:02d}"


def _location(chunk) -> str | None:
    prov, meta = chunk.provenance, chunk.metadata
    if chunk.chunk_type == "transcript" and prov.start_ms is not None:
        line = f"Time: {_clock(prov.start_ms)}–{_clock(prov.end_ms or prov.start_ms)}"
        speakers = meta.get("speakers") or ([prov.speaker] if prov.speaker else [])
        return line + (f" ({', '.join(speakers)})" if speakers else "")
    if chunk.chunk_type == "sheet_rows":
        sheet = f"Sheet: {prov.sheet}" if prov.sheet else "Table"
        if meta.get("summary"):
            return f"{sheet} (summary)"
        return f"{sheet}, rows {prov.row_range[0]}–{prov.row_range[1]}" if prov.row_range else sheet
    if prov.slide is not None:
        end = meta.get("slide_end")
        return f"Slides: {prov.slide}–{end}" if end else f"Slide: {prov.slide}"
    if prov.page is not None:
        end = meta.get("page_end")
        return f"Pages: {prov.page}–{end}" if end else f"Page: {prov.page}"
    return None
