"""
Spreadsheets (spec stage 6): per sheet, one summary chunk (columns, row count,
a few sample rows) followed by row groups that each repeat the header row.
Row-group provenance carries the exact spreadsheet row numbers.
"""

from __future__ import annotations

from rag_ingest.chunkers.base import Budget, make_chunk
from rag_ingest.extractors.spreadsheet import markdown_table
from rag_ingest.llm.tokenizer import Tokenizer, truncate
from rag_ingest.models import Chunk, DocumentModel, Element, Provenance

SAMPLE_ROWS = 3


class SheetChunker:
    def __init__(self, tokenizer: Tokenizer, budget: Budget) -> None:
        self.tokenizer = tokenizer
        self.budget = budget
        self.name = "sheet"
        self.version = "1"

    @property
    def key(self) -> str:
        return f"{self.name}:{self.version}"

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]:
        tok, b = self.tokenizer, self.budget
        chunks: list[Chunk] = []

        def emit(text: str, elements: list[Element], sheet: str | None, rows: tuple[int, int] | None,
                 **metadata) -> None:
            chunk = make_chunk(model, tenant_id, self.key, len(chunks), text, elements,
                               [sheet] if sheet else [], "sheet_rows", tok, sheet=sheet, **metadata)
            chunk.provenance = Provenance(sheet=sheet, row_range=rows)
            chunks.append(chunk)

        for sheet, elements in self._by_sheet(model.elements):
            header = elements[0].attributes["header"]
            rows: list[tuple[int, list[str], Element]] = []
            for element in elements:
                lines = element.text.split("\n")[2:]
                rows.extend(zip(element.attributes["row_numbers"], lines, [element] * len(lines)))

            # The summary: what the sheet is, for questions about the sheet rather than a row.
            label = f"Sheet: {sheet}" if sheet else "Table"
            summary = "\n".join([
                label,
                f"Columns: {', '.join(c for c in header if c)}",
                f"Rows: {len(rows)}",
                "Sample rows:",
                markdown_table(header, []) + "".join("\n" + line for _, line, _ in rows[:SAMPLE_ROWS]),
            ])
            emit(summary, elements[:1], sheet, (rows[0][0], rows[-1][0]) if rows else None, summary=True)

            # Row groups, the header repeated on each.
            head = markdown_table(header, [])
            room = max(1, b.max - tok.count(head) - 1)
            group: list[tuple[int, str, Element]] = []
            size = 0
            for number, line, element in rows:
                n = tok.count(line) + 1
                if n > room:
                    # One row wider than a whole chunk: keep what fits rather than fail the sheet.
                    line, n = truncate(tok, line, room - 1), room
                if group and size + n > room:
                    self._flush(group, head, emit, sheet)
                    group, size = [], 0
                group.append((number, line, element))
                size += n
            if group:
                self._flush(group, head, emit, sheet)
        return chunks

    @staticmethod
    def _flush(group, head, emit, sheet) -> None:
        members: list[Element] = []
        for _, _, element in group:
            if element not in members:
                members.append(element)
        text = head + "".join("\n" + line for _, line, _ in group)
        emit(text, members, sheet, (group[0][0], group[-1][0]))

    @staticmethod
    def _by_sheet(elements: list[Element]):
        current: list[Element] = []
        for element in elements:
            if current and element.provenance.sheet != current[0].provenance.sheet:
                yield current[0].provenance.sheet, current
                current = []
            current.append(element)
        if current:
            yield current[0].provenance.sheet, current

