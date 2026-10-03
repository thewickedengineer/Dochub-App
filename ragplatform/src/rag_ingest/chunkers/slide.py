"""
Presentations (spec stage 6): one chunk per slide — title, body and speaker
notes together. A tiny slide (a divider, "Questions?") is merged with its
neighbour in the same section; a slide too long for one chunk is split with its
title repeated on each part.
"""

from __future__ import annotations

from rag_ingest.chunkers.base import Budget, make_chunk, split_recursive
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.models import Chunk, DocumentModel, Element


class SlideChunker:
    def __init__(self, tokenizer: Tokenizer, budget: Budget) -> None:
        self.tokenizer = tokenizer
        self.budget = budget
        self.name = "slide"
        self.version = "1"

    @property
    def key(self) -> str:
        return f"{self.name}:{self.version}"

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]:
        tok, b = self.tokenizer, self.budget
        groups: list[tuple[str, list[Element]]] = []

        for element in model.elements:
            title = element.attributes.get("title") or ""
            if tok.count(element.text) > b.max:
                body = element.text[len(title):].lstrip() if title and element.text.startswith(title) else element.text
                room = max(1, b.max - tok.count(title) - 1)
                for part in split_recursive(tok, body, room):
                    groups.append((f"{title}\n{part}" if title else part, [element]))
                continue

            if groups:
                text, members = groups[-1]
                same_section = members[-1].provenance.section_path == element.provenance.section_path
                tiny = tok.count(text) < b.min or tok.count(element.text) < b.min
                merged = f"{text}\n\n{element.text}"
                if same_section and tiny and tok.count(merged) <= b.max:
                    groups[-1] = (merged, members + [element])
                    continue
            groups.append((element.text, [element]))

        chunks = []
        for ordinal, (text, members) in enumerate(groups):
            slides = [m.provenance.slide for m in members if m.provenance.slide is not None]
            chunks.append(make_chunk(model, tenant_id, self.key, ordinal, text, members,
                                     members[0].provenance.section_path, "slide", tok,
                                     slide_end=max(slides) if len(set(slides)) > 1 else None))
        return chunks
