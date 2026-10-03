"""Shared chunking primitives. All sizes are tokens in the embedding tokenizer."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Protocol

from rag_ingest.ids import chunk_id
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.models import Chunk, DocumentModel, Element, Provenance

_SENTENCE = re.compile(r"(?<=[.!?])\s+(?=[A-Z0-9\"'(\[])")


@dataclass(frozen=True)
class Budget:
    target: int
    max: int
    min: int
    overlap: int


class Chunker(Protocol):
    name: str
    version: str

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]: ...


def split_recursive(tokenizer: Tokenizer, text: str, max_tokens: int) -> list[str]:
    """Split on paragraph → line → sentence → word until every piece fits."""
    if tokenizer.count(text) <= max_tokens:
        return [text]
    for splitter in (lambda t: t.split("\n\n"), lambda t: t.split("\n"),
                     lambda t: _SENTENCE.split(t), lambda t: t.split(" ")):
        parts = [p for p in splitter(text) if p.strip()]
        if len(parts) > 1:
            out: list[str] = []
            for part in parts:
                out.extend(split_recursive(tokenizer, part, max_tokens))
            return out
    # One unbreakable run (a giant token, a base64 blob): cut by tokens.
    ids = tokenizer.encode(text)
    return [tokenizer.decode(ids[i:i + max_tokens]) for i in range(0, len(ids), max_tokens)]


def merge_pieces(tokenizer: Tokenizer, pieces: list[str], budget: Budget, joiner: str = "\n\n",
                 overlap: bool = True) -> list[str]:
    """Greedy merge up to target, carrying ~overlap tokens of tail into the next chunk."""
    chunks: list[str] = []
    current: list[str] = []
    current_tokens = 0
    sizes = [tokenizer.count(p) for p in pieces]

    for piece, size in zip(pieces, sizes):
        joined = current_tokens + size + (1 if current else 0)
        if current and joined > budget.target:
            chunks.append(joiner.join(current))
            if overlap and budget.overlap > 0:
                carry, carried = [], 0
                for prev in reversed(current):
                    n = tokenizer.count(prev)
                    if carried + n > budget.overlap:
                        break
                    carry.insert(0, prev)
                    carried += n
                # Never let the carry plus the next piece break the hard max.
                if carried + size > budget.max:
                    carry, carried = [], 0
                current, current_tokens = carry, carried
            else:
                current, current_tokens = [], 0
        current.append(piece)
        current_tokens += size + (1 if len(current) > 1 else 0)
    if current:
        chunks.append(joiner.join(current))
    return chunks


def split_lines(tokenizer: Tokenizer, text: str, max_tokens: int) -> list[str]:
    """Code and tables: group whole lines, no overlap, never split a line unless forced."""
    out: list[str] = []
    current: list[str] = []
    current_tokens = 0
    for line in text.split("\n"):
        n = tokenizer.count(line) + 1
        if n > max_tokens:
            if current:
                out.append("\n".join(current))
                current, current_tokens = [], 0
            out.extend(split_recursive(tokenizer, line, max_tokens))
            continue
        if current and current_tokens + n > max_tokens:
            out.append("\n".join(current))
            current, current_tokens = [], 0
        current.append(line)
        current_tokens += n
    if current:
        out.append("\n".join(current))
    return [part for part in out if part.strip()]


def merged_provenance(elements: list[Element]) -> Provenance:
    """The span a chunk covers: first page/slide, first..last line, and the box when it is one element."""
    pages = [e.provenance.page for e in elements if e.provenance.page is not None]
    slides = [e.provenance.slide for e in elements if e.provenance.slide is not None]
    lines = [e.provenance.line_range for e in elements if e.provenance.line_range]
    rows = [e.provenance.row_range for e in elements if e.provenance.row_range]
    first = elements[0].provenance if elements else Provenance()
    return Provenance(
        page=min(pages) if pages else None,
        slide=min(slides) if slides else None,
        sheet=first.sheet,
        row_range=(min(r[0] for r in rows), max(r[1] for r in rows)) if rows else None,
        section_path=first.section_path,
        file_path=first.file_path,
        line_range=(min(r[0] for r in lines), max(r[1] for r in lines)) if lines else None,
        bbox=first.bbox if len(elements) == 1 else None,
    )


def page_end(elements: list[Element]) -> int | None:
    """Last page of a chunk that crosses a page boundary (Provenance holds only the first)."""
    pages = {e.provenance.page for e in elements if e.provenance.page is not None}
    return max(pages) if len(pages) > 1 else None


def make_chunk(model: DocumentModel, tenant_id: str, chunker: str, ordinal: int, text: str,
               elements: list[Element], heading_path: list[str], chunk_type: str,
               tokenizer: Tokenizer, **metadata) -> Chunk:
    return Chunk(
        chunk_id=chunk_id(model.document_id, chunker, ordinal),
        document_id=model.document_id,
        tenant_id=tenant_id,
        ordinal=ordinal,
        text=text,
        contextualized_text=text,  # completed by the contextualize stage
        token_count=tokenizer.count(text),
        element_ids=[e.id for e in elements],
        provenance=merged_provenance(elements),
        heading_path=heading_path,
        chunk_type=chunk_type,
        metadata={k: v for k, v in {"page_end": page_end(elements), **metadata}.items() if v is not None},
    )
