"""
Structure-aware chunking (spec stage 6) for Markdown, plain text, and — as the
"hierarchical" chunker — PDF, Word, HTML and OCR'd images: group elements under
their heading, fill up to the target, never split a table, list item or code
block, and only overlap when a long paragraph has to be cut.

With `tables_alone`, every table is its own chunk (`chunk_type=table`), split
into row groups that repeat the header when it is too large.
"""

from __future__ import annotations

from rag_ingest.chunkers.base import (
    Budget, make_chunk, merge_pieces, split_lines, split_recursive,
)
from rag_ingest.chunkers.table import split_table
from rag_ingest.llm.tokenizer import Tokenizer
from rag_ingest.models import Chunk, DocumentModel, Element


def _titled(piece) -> str:
    """A part's own section heading as its first line, unless it already starts with it."""
    text, _, path, _ = piece
    if not path or text.startswith(path[-1]):
        return text
    return f"{path[-1]}\n{text}"


class StructuredChunker:
    def __init__(self, tokenizer: Tokenizer, budget: Budget, name: str, split_levels: int,
                 tables_alone: bool = False) -> None:
        self.tokenizer = tokenizer
        self.budget = budget
        self.name = name
        self.version = "3"
        self.split_levels = split_levels
        self.tables_alone = tables_alone

    @property
    def key(self) -> str:
        return f"{self.name}:{self.version}"

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]:
        pieces: list[tuple[str, list[Element], list[str], str]] = []   # text, elements, path, type

        for path, elements in self._sections(model.elements):
            pieces.extend(self._pack(elements, path))

        pieces = self._merge_small(pieces)
        return [
            make_chunk(model, tenant_id, self.key, ordinal, text, elements, path, kind, self.tokenizer)
            for ordinal, (text, elements, path, kind) in enumerate(pieces)
        ]

    # ── sections ───────────────────────────────────────────────────────────────

    def _sections(self, elements: list[Element]):
        current: list[Element] = []
        path: list[str] = []
        for element in elements:
            # A document title opens a section too (level 0); its text then leads every chunk path below it.
            is_title = element.type == "title"
            if (element.type == "heading" and (element.level or 1) <= self.split_levels) or \
                    (is_title and self.split_levels > 0):
                if current:
                    yield path, current
                path = element.provenance.section_path + [element.text]
                current = []
                continue
            current.append(element)
        if current:
            yield path, current

    # ── packing ────────────────────────────────────────────────────────────────

    def _units(self, element: Element) -> list[tuple[str, str, bool]]:
        """Split one element into (text, chunk_type, overlap_allowed) units that each fit max."""
        tok, b = self.tokenizer, self.budget
        body = self._render(element)
        if tok.count(body) <= b.max:
            return [(body, self._type(element), False)]

        if element.type == "table":
            return [(part, "table", False) for part in split_table(tok, element.text, b.max)]

        if element.type == "list" and element.attributes.get("items"):
            items = element.attributes["items"]
            units: list[tuple[str, str, bool]] = []
            for group in merge_pieces(tok, [p for item in items for p in split_recursive(tok, item, b.max)],
                                      Budget(b.max, b.max, b.min, 0), joiner="\n", overlap=False):
                units.append((group, "prose", False))
            return units

        if element.type == "code":
            fence = f"```{element.language or ''}"
            room = max(1, b.max - tok.count(fence) - tok.count("```") - 2)
            return [(f"{fence}\n{part}\n```", "code", False) for part in split_lines(tok, element.text, room)]

        return [(part, "prose", True) for part in split_recursive(tok, body, b.max)]

    def _pack(self, elements: list[Element], path: list[str]):
        tok, b = self.tokenizer, self.budget
        out = []
        buffer: list[str] = []
        buffer_elements: list[Element] = []
        buffer_types: set[str] = set()
        size = 0

        def flush():
            nonlocal buffer, buffer_elements, buffer_types, size
            if buffer:
                kind = next(iter(buffer_types)) if len(buffer_types) == 1 else "prose"
                out.append(("\n\n".join(buffer), list(buffer_elements), path, kind))
            buffer, buffer_elements, buffer_types, size = [], [], set(), 0

        for element in elements:
            if self.tables_alone and element.type == "table":
                flush()
                caption = element.attributes.get("caption")
                for part in split_table(tok, element.text, b.max - (tok.count(caption) + 1 if caption else 0)):
                    out.append((f"{caption}\n{part}" if caption else part, [element], path, "table"))
                continue
            units = self._units(element)
            # A long paragraph cut into several units gets the prose overlap rule.
            if len(units) > 1 and units[0][2]:
                flush()
                for text in merge_pieces(tok, [u[0] for u in units], b):
                    out.append((text, [element], path, "prose"))
                continue
            for text, kind, _ in units:
                n = tok.count(text)
                # Fill to target; allow up to max rather than leave a tiny chunk behind.
                limit = b.max if size < b.min else b.target
                if buffer and size + n + 2 > limit:
                    flush()
                buffer.append(text)
                buffer_elements.append(element)
                buffer_types.add(kind)
                size += n + 2
        flush()
        return out

    def _merge_small(self, pieces):
        """
        Fold undersized chunks into a neighbour:

        - an undersized chunk into the next one in the same or a sibling section.
          Across sections the merged chunk is cited by their common parent and
          each part keeps its own heading as a line — otherwise the first
          section's text would be cited under the second's name;
        - an undersized subsection into its parent's chunk just before it. The
          parent's path is still a true citation for it; a full-sized
          subsection keeps its own, more precise one. The root (no heading) is
          not a parent for this purpose.
        """
        tok, b = self.tokenizer, self.budget
        merged = []
        for piece in pieces:
            if merged:
                prev = merged[-1]
                standalone = self.tables_alone and "table" in (prev[3], piece[3])
                kind = prev[3] if prev[3] == piece[3] else "prose"
                same_section = prev[2] == piece[2]
                siblings = len(prev[2]) == len(piece[2]) and prev[2][:-1] == piece[2][:-1]
                # A real parent heading — not the document root, which every section sits under.
                child = bool(prev[2]) and len(piece[2]) > len(prev[2]) and piece[2][:len(prev[2])] == prev[2]

                if not standalone and child and tok.count(piece[0]) < b.min:
                    text = prev[0] + "\n\n" + _titled(piece)
                    # Up to the target only, so one parent does not swallow every short subsection.
                    if tok.count(text) <= b.target:
                        merged[-1] = (text, prev[1] + piece[1], prev[2], kind)
                        continue

                if not standalone and tok.count(prev[0]) < b.min and (same_section or siblings):
                    if same_section:
                        text, path = prev[0] + "\n\n" + piece[0], prev[2]
                    else:
                        text = "\n\n".join(_titled(p) for p in (prev, piece))
                        path = prev[2][:-1]
                    if tok.count(text) <= b.max:
                        merged[-1] = (text, prev[1] + piece[1], path, kind)
                        continue
            merged.append(piece)
        return merged

    @staticmethod
    def _render(element: Element) -> str:
        if element.type == "code":
            return f"```{element.language or ''}\n{element.text}\n```"
        if element.type == "heading":
            return element.text
        return element.text

    @staticmethod
    def _type(element: Element) -> str:
        return {"table": "table", "code": "code"}.get(element.type, "prose")
