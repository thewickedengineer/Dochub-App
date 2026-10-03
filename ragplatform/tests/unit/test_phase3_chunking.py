"""Phase 3, no models needed: tables, spreadsheets, slides, detection and the quality gate."""

import io
import zipfile

import pytest

from rag_ingest.chunkers.base import Budget
from rag_ingest.chunkers.sheet import SheetChunker
from rag_ingest.chunkers.slide import SlideChunker
from rag_ingest.chunkers.structured import StructuredChunker
from rag_ingest.chunkers.table import split_table
from rag_ingest.extractors import spreadsheet
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.pipeline.errors import PermanentError
from rag_ingest.pipeline.stages.detect import classify
from rag_ingest.pipeline.stages.normalize import NormalizeStage, detect_language
from support.offline import FIXTURES, TOKENIZER as TOK, offline_settings, run_offline

HEADER = "| Grade | Limit | Countersign |\n|---|---|---|"
TABLE = HEADER + "\n" + "\n".join(f"| G{i} | {i * 2500} | {'No' if i < 30 else 'Yes'} |" for i in range(1, 61))


def _model(elements: list[Element], family="pdf") -> DocumentModel:
    return DocumentModel(document_id="doc_t", content_hash="h", content_type="x", content_family=family,
                         elements=elements)


def _el(i: int, kind: str, text: str, **kwargs) -> Element:
    return Element(id=element_id("doc_t", i), type=kind, text=text, **kwargs)


# ── Tables ───────────────────────────────────────────────────────────────────

def test_a_split_table_repeats_its_header_in_every_part_and_never_cuts_a_row():
    parts = split_table(TOK, TABLE, 120)
    assert len(parts) > 2
    rows = []
    for part in parts:
        assert part.startswith(HEADER + "\n")
        assert TOK.count(part) <= 120
        rows.extend(part.split("\n")[2:])
    assert rows == TABLE.split("\n")[2:]             # every row exactly once, in order


def test_a_table_is_its_own_chunk_in_documents():
    chunker = StructuredChunker(TOK, Budget(450, 120, 80, 0), "hierarchical", 3, tables_alone=True)
    elements = [_el(0, "heading", "Authority", level=1),
                _el(1, "paragraph", "Limits below.", provenance=Provenance(section_path=["Authority"])),
                _el(2, "table", TABLE, provenance=Provenance(section_path=["Authority"]),
                    attributes={"caption": "Table 1: settlement limits"})]
    chunks = chunker.chunk(_model(elements), "t")
    tables = [c for c in chunks if c.chunk_type == "table"]
    assert len(tables) > 1 and all(c.heading_path == ["Authority"] for c in tables)
    assert all(c.text.startswith("Table 1: settlement limits\n" + HEADER) for c in tables)
    assert all(c.token_count <= 120 for c in tables)
    # The short paragraph before it was not glued onto the table.
    assert [c.chunk_type for c in chunks][0] == "prose"


def test_a_title_opens_a_section_and_an_intro_merges_with_its_first_subsection():
    chunker = StructuredChunker(TOK, Budget(450, 800, 80, 60), "hierarchical", 3, tables_alone=True)
    elements = [
        _el(0, "title", "Claims Guide", level=0),
        _el(1, "heading", "1 Intake", level=1),
        _el(2, "paragraph", "Acknowledge every claim.", provenance=Provenance(section_path=["1 Intake"])),
        _el(3, "heading", "1.1 Documents", level=2, provenance=Provenance(section_path=["1 Intake"])),
        _el(4, "list", "- Claim form\n- Photos", provenance=Provenance(section_path=["1 Intake", "1.1 Documents"])),
    ]
    [chunk] = chunker.chunk(_model(elements), "t")
    assert chunk.heading_path == ["1 Intake"]          # cited by the parent, not the subsection
    assert chunk.text == "Acknowledge every claim.\n\n1.1 Documents\n- Claim form\n- Photos"


# ── Spreadsheets ─────────────────────────────────────────────────────────────

def test_xlsx_skips_empty_sheets_and_records_header_and_row_numbers():
    raw = (FIXTURES / "claims-register.xlsx").read_bytes()
    model = spreadsheet.extract("doc_t", "h", "x", raw, "claims-register.xlsx", offline_settings())
    assert [s["sheet"] for s in model.metadata["sheets"]] == ["Claims", "Rates"]   # "Empty" skipped
    first = model.elements[0]
    assert first.attributes["header"] == ["Claim", "Line", "Status", "Reserve"]
    assert first.attributes["row_numbers"][0] == 2 and first.provenance.row_range == (2, 51)
    assert "| CLM-1002 | Home | Closed | 5400.5 |" in first.text


def test_csv_is_read_with_its_delimiter():
    raw = b"Claim;Status\nCLM-1;Open\nCLM-2;Closed\n"
    model = spreadsheet.extract("doc_t", "h", "x", raw, "claims.csv", offline_settings())
    assert model.elements[0].text.split("\n")[2] == "| CLM-1 | Open |"


def test_a_legacy_workbook_is_refused_with_a_reason():
    with pytest.raises(PermanentError) as error:
        spreadsheet.extract("doc_t", "h", "x", b"\xd0\xcf\x11\xe0", "old.xls", offline_settings())
    assert error.value.reason == "unsupported_format" and ".xlsx" in str(error.value)


def test_sheet_chunks_are_a_summary_then_row_groups_each_with_the_header():
    raw = (FIXTURES / "claims-register.xlsx").read_bytes()
    model = spreadsheet.extract("doc_t", "h", "x", raw, "claims-register.xlsx", offline_settings())
    chunks = SheetChunker(TOK, Budget(150, 150, 20, 0)).chunk(model, "t")

    claims = [c for c in chunks if c.provenance.sheet == "Claims"]
    summary, groups = claims[0], claims[1:]
    assert summary.metadata["summary"] and "Rows: 60" in summary.text and "Columns: Claim, Line" in summary.text
    assert len(groups) > 2
    header = "| Claim | Line | Status | Reserve |\n| --- | --- | --- | --- |"
    assert all(g.text.startswith(header) and g.token_count <= 150 for g in groups)
    # Row ranges tile the sheet with no gap and no overlap.
    ranges = [g.provenance.row_range for g in groups]
    assert ranges[0][0] == 2 and ranges[-1][1] == 61
    assert all(b[0] == a[1] + 1 for a, b in zip(ranges, ranges[1:]))
    assert all(c.chunk_type == "sheet_rows" for c in chunks)


# ── Slides ───────────────────────────────────────────────────────────────────

def _slide(i: int, text: str, title: str | None = None) -> Element:
    return _el(i, "slide", text, provenance=Provenance(slide=i + 1), attributes={"title": title})


def test_one_chunk_per_slide_with_a_divider_merged_into_its_neighbour():
    elements = [_slide(0, "Fraud indicators\n\n- Late notification\n- Inconsistent accounts\n\n"
                          "Speaker notes: no single indicator proves fraud.", "Fraud indicators"),
                _slide(1, "Referral\n\n- Refer to SIU within 48 hours, with the file and a summary.", "Referral"),
                _slide(2, "Questions?", "Questions?")]
    chunks = SlideChunker(TOK, Budget(600, 600, 10, 0)).chunk(_model(elements, "presentation"), "t")
    assert [c.provenance.slide for c in chunks] == [1, 2]
    assert chunks[0].text.endswith("Speaker notes: no single indicator proves fraud.")
    assert chunks[1].text.endswith("Questions?") and chunks[1].metadata["slide_end"] == 3


def test_an_oversized_slide_is_split_with_its_title_on_every_part():
    body = "\n".join(f"- Point {i} about the referral process and what to include." for i in range(80))
    elements = [_slide(0, f"Referral\n\n{body}", "Referral")]
    chunks = SlideChunker(TOK, Budget(100, 100, 10, 0)).chunk(_model(elements, "presentation"), "t")
    assert len(chunks) > 1
    assert all(c.text.startswith("Referral\n") and c.token_count <= 100 for c in chunks)
    assert all(c.provenance.slide == 1 for c in chunks)


# ── Detection and quality ────────────────────────────────────────────────────

def test_office_packages_are_told_apart_by_their_contents_not_their_name():
    # Every OOXML file is a zip; magic numbers alone call them all Word documents.
    assert classify("x.bin", (FIXTURES / "claims-register.xlsx").read_bytes())[1] == "spreadsheet"
    assert classify("x.bin", (FIXTURES / "fraud-training.pptx").read_bytes())[1] == "presentation"
    assert classify("x.bin", (FIXTURES / "claims-guide.docx").read_bytes())[1] == "office"
    plain = io.BytesIO()
    with zipfile.ZipFile(plain, "w") as archive:
        archive.writestr("readme.txt", "hello")
    with pytest.raises(PermanentError):
        classify("bundle.zip", plain.getvalue())


async def test_a_legacy_office_file_fails_with_advice():
    with pytest.raises(PermanentError) as error:
        await run_offline("old.doc", raw=b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1" + b"\x00" * 600)
    assert error.value.reason == "unsupported_format" and ".docx" in str(error.value)


async def test_low_ocr_confidence_fails_the_document_instead_of_indexing_it():
    from datetime import datetime, timezone

    from rag_ingest.models import IngestMessage
    from rag_ingest.pipeline.context import PipelineContext

    ctx = PipelineContext(
        message=IngestMessage(message_id="m", tenant_id="t", source="upload", source_item_id="s",
                              source_uri="u", source_version=None, enqueued_at=datetime.now(timezone.utc)),
        document_id="doc_t")
    ctx.family = "pdf"
    ctx.model = _model([_el(0, "paragraph", "W1tn3ss st@tement l saw th3 b1ue v@n " * 3)])
    ctx.model.extraction = {"ocr_confidence": 0.31}
    with pytest.raises(PermanentError) as error:
        await NormalizeStage(offline_settings(min_ocr_confidence=0.5)).run(ctx)
    assert error.value.reason == "low_quality_extraction" and "0.31" in str(error.value)


def test_english_markdown_is_detected_as_english():
    # Low-accuracy detection labelled this file Welsh, which disabled stemming for it.
    assert detect_language((FIXTURES / "README.md").read_text()) == "en"
    assert detect_language("Schäden müssen innerhalb von fünf Werktagen gemeldet werden, sonst entfällt der Anspruch.") == "de"


async def test_csv_runs_end_to_end_without_a_language_guess():
    ctx = await run_offline("reserves.csv")
    assert ctx.model.language is None and ctx.ts_configs["*"] == "english"
    assert ctx.chunks[1].contextualized_text.split("\n\n")[0].endswith("Table, rows 2–5")
