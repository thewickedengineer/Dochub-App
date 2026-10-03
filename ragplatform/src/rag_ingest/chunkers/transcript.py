"""
Transcripts (spec stage 6): group Whisper segments into windows of roughly
60–120 seconds and at most 600 tokens. Once a window has reached the minimum
length it closes at the next natural break — a change of speaker or a pause —
and it always closes at the maximum. Each window after the first starts with
the previous window's last segment (~1 segment of overlap), so a sentence cut
by a window edge is still found whole. Every chunk keeps `start_ms`/`end_ms`
for deep links into the recording.
"""

from __future__ import annotations

from dataclasses import dataclass

from rag_ingest.chunkers.base import Budget, make_chunk, split_recursive
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.models import Chunk, DocumentModel, Element, Provenance


@dataclass(frozen=True)
class Windows:
    min_ms: int
    max_ms: int
    pause_ms: int


def _line(element: Element, previous: Element | None) -> str:
    speaker = element.provenance.speaker
    if speaker and (previous is None or previous.provenance.speaker != speaker):
        return f"{speaker}: {element.text}"
    return element.text


class TranscriptChunker:
    def __init__(self, tokenizer: Tokenizer, budget: Budget, windows: Windows) -> None:
        self.tokenizer = tokenizer
        self.budget = budget
        self.windows = windows
        self.name = "transcript"
        self.version = "1"

    @property
    def key(self) -> str:
        return f"{self.name}:{self.version}"

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]:
        segments = self._fit(model.elements)
        windows: list[list[Element]] = []
        current: list[Element] = []
        tokens = 0

        for segment in segments:
            n = self.tokenizer.count(segment.text) + 1
            if current:
                start = current[0].provenance.start_ms or 0
                length = (segment.provenance.end_ms or 0) - start
                gap = (segment.provenance.start_ms or 0) - (current[-1].provenance.end_ms or 0)
                turn = segment.provenance.speaker != current[-1].provenance.speaker
                long_enough = (current[-1].provenance.end_ms or 0) - start >= self.windows.min_ms
                if (tokens + n > self.budget.max or length > self.windows.max_ms
                        or (long_enough and (turn or gap >= self.windows.pause_ms))):
                    windows.append(current)
                    # ~1 segment of overlap, unless that alone would break the budget.
                    carry = current[-1]
                    carry_n = self.tokenizer.count(carry.text) + 1
                    current, tokens = ([carry], carry_n) if carry_n + n <= self.budget.max else ([], 0)
            current.append(segment)
            tokens += n
        if current:
            windows.append(current)

        chunks = []
        for ordinal, members in enumerate(windows):
            text = "\n".join(_line(e, members[i - 1] if i else None) for i, e in enumerate(members))
            chunk = make_chunk(model, tenant_id, self.key, ordinal, text, members, [], "transcript",
                               self.tokenizer)
            speakers = sorted({e.provenance.speaker for e in members if e.provenance.speaker})
            chunk.provenance = Provenance(
                start_ms=members[0].provenance.start_ms, end_ms=members[-1].provenance.end_ms,
                speaker=speakers[0] if len(speakers) == 1 else None)
            if len(speakers) > 1:
                chunk.metadata["speakers"] = speakers
            chunks.append(chunk)
        return chunks

    def _fit(self, elements: list[Element]) -> list[Element]:
        """A single segment longer than the budget (rare) is cut, sharing its time span pro rata."""
        out: list[Element] = []
        for element in elements:
            if self.tokenizer.count(element.text) < self.budget.max:
                out.append(element)
                continue
            parts = split_recursive(self.tokenizer, element.text, self.budget.max - 1)
            start, end = element.provenance.start_ms or 0, element.provenance.end_ms or 0
            step = (end - start) / len(parts)
            for i, part in enumerate(parts):
                out.append(element.model_copy(update={
                    "text": part,
                    "provenance": element.provenance.model_copy(update={
                        "start_ms": int(start + i * step), "end_ms": int(start + (i + 1) * step)}),
                }))
        return out
