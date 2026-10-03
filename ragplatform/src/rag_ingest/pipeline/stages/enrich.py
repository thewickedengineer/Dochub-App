"""
Stage 5 — title and metadata, and one LLM call per document for a 3–5 sentence
summary. The summary is stored on the document (document-level search) and is
handed to stage 7 with the document. Short code files get no summary (spec).
"""

from __future__ import annotations

from pathlib import PurePosixPath

from rag_ingest.config import Settings
from rag_ingest.llm.client import LLMClient
from rag_ingest.llm.prompts import SUMMARY_PROMPT, SUMMARY_SYSTEM, document_text
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.pipeline.context import PipelineContext


class EnrichStage:
    name = "enrich"

    def __init__(self, settings: Settings | None = None, llm: LLMClient | None = None,
                 tokenizer: Tokenizer | None = None) -> None:
        self.settings = settings
        self.llm = llm if settings is not None and settings.summary_enabled else None
        self.tokenizer = tokenizer
        self.version = "2-llm" if self.llm else "1"

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        model = ctx.model
        assert model is not None
        path = ctx.hint("relative_path") or ctx.filename

        if not model.title:
            first_heading = next((e.text for e in model.elements if e.type == "heading"), None)
            model.title = first_heading or PurePosixPath(path).stem.replace("_", " ").replace("-", " ").strip() \
                or ctx.filename

        # Lines only mean something for text; for a PDF or a recording they are noise.
        textual = ctx.family in ("text", "markdown", "code")
        line_count = (ctx.raw or b"").count(b"\n") + 1 if textual else None
        model.metadata.update({
            "filename": ctx.filename,
            "relative_path": path,
            "artifact_id": ctx.hint("artifact_id"),
            "artifact_name": ctx.hint("artifact_name"),
            "team_name": ctx.hint("team_name"),
            "group_name": ctx.hint("group_name"),
            "source_reference": ctx.hint("source_reference"),
            "dochub_document_id": ctx.hint("dochub_document_id"),
            "revision": int(ctx.hint("revision", "1") or 1),
            "content_md5": ctx.hint("content_md5"),
            "size_bytes": len(ctx.raw or b""),
            "line_count": line_count,
            "element_count": len(model.elements),
        })
        if line_count is None:
            model.metadata.pop("line_count", None)

        if self.llm is not None and self._wants_summary(ctx, line_count):
            text, truncated = document_text(model, self.tokenizer, self.settings.llm_max_document_tokens)
            result = await self.llm.complete(purpose="summary", system=SUMMARY_SYSTEM, prompt=SUMMARY_PROMPT,
                                             document=text, cache_key=ctx.document_id,
                                             max_tokens=self.settings.summary_max_tokens)
            ctx.summary = result.text.strip() or None
            ctx.llm_usage.add(result.usage)
            if truncated:
                ctx.warnings.append(f"summary written from the first {self.settings.llm_max_document_tokens} tokens")
        return ctx

    def _wants_summary(self, ctx: PipelineContext, line_count: int | None) -> bool:
        if ctx.family == "code" and (line_count or 0) < self.settings.summary_min_code_lines:
            return False
        return bool(ctx.model and ctx.model.elements)
        return ctx
