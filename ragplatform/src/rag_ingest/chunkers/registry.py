"""Picks the chunker for a content family."""

from __future__ import annotations

from rag_ingest.chunkers.base import Budget, Chunker
from rag_ingest.chunkers.code_ast import CodeChunker
from rag_ingest.chunkers.sheet import SheetChunker
from rag_ingest.chunkers.slide import SlideChunker
from rag_ingest.chunkers.structured import StructuredChunker
from rag_ingest.chunkers.transcript import TranscriptChunker, Windows
from rag_ingest.config import Settings
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.pipeline.errors import PermanentError

# Room kept for the contextual header, so header + chunk fit the model's input.
PROSE_HEADER_TOKENS = 64
CODE_HEADER_TOKENS = 200


def budget_for(settings: Settings, model_max: int, header_tokens: int, max_tokens: int | None = None) -> Budget:
    hard = min(max_tokens or settings.chunk_max_tokens, model_max - header_tokens)
    if hard < 64:
        raise ValueError(f"The embedding model accepts {model_max} tokens — too few for chunks plus context.")
    target = min(settings.chunk_target_tokens, hard)
    return Budget(target=target, max=hard, min=min(settings.chunk_min_tokens, target // 2),
                  overlap=min(settings.chunk_overlap_tokens, target // 5))


class ChunkerRegistry:
    def __init__(self, settings: Settings, tokenizer: Tokenizer, model_max_tokens: int) -> None:
        prose = budget_for(settings, model_max_tokens, PROSE_HEADER_TOKENS)
        code = budget_for(settings, model_max_tokens, CODE_HEADER_TOKENS)
        slides = budget_for(settings, model_max_tokens, PROSE_HEADER_TOKENS, settings.slide_max_tokens)
        slides = Budget(slides.target, slides.max, min(settings.slide_min_tokens, slides.max), 0)
        sheets = budget_for(settings, model_max_tokens, PROSE_HEADER_TOKENS, settings.sheet_max_tokens)
        speech = budget_for(settings, model_max_tokens, PROSE_HEADER_TOKENS, settings.transcript_max_tokens)
        windows = Windows(settings.transcript_min_window_seconds * 1000, settings.transcript_max_window_seconds * 1000,
                          settings.transcript_pause_ms)
        hierarchical = StructuredChunker(tokenizer, prose, "hierarchical", settings.document_split_levels,
                                         tables_alone=True)
        self._by_family: dict[str, Chunker] = {
            "markdown": StructuredChunker(tokenizer, prose, "markdown", settings.markdown_split_levels),
            "text": StructuredChunker(tokenizer, prose, "recursive", split_levels=0),
            "code": CodeChunker(tokenizer, code),
            "pdf": hierarchical, "office": hierarchical, "html": hierarchical, "image": hierarchical,
            "presentation": SlideChunker(tokenizer, slides),
            "spreadsheet": SheetChunker(tokenizer, sheets),
            "audio_video": TranscriptChunker(tokenizer, speech, windows),
        }

    def fingerprint(self, family: str) -> str:
        """The chunker name and version for a family — part of what 'already indexed' means."""
        chunker = self.for_family(family)
        return f"{chunker.name}:{chunker.version}"

    def for_family(self, family: str) -> Chunker:
        chunker = self._by_family.get(family)
        if chunker is None:
            raise PermanentError("no_chunker", f"no chunker for content family '{family}'")
        return chunker
