"""
Phase 3 through Docling: Word, PowerPoint, HTML, a text PDF, a scanned PDF and
an image. Slow — the first test in a session loads (and on a fresh machine
downloads) the layout, table and OCR models.
"""

import pytest

from rag_ingest.pipeline.errors import PermanentError
from support.offline import FIXTURES, offline_settings, run_offline

pytestmark = pytest.mark.slow

SMALL_CHUNKS = offline_settings(chunk_max_tokens=200, chunk_target_tokens=150)


async def test_word_headings_become_section_paths_and_a_long_table_repeats_its_header():
    ctx = await run_offline("claims-guide.docx", settings=SMALL_CHUNKS)
    model = ctx.model
    assert model.title == "Claims Handling Guide" and model.metadata["author"] == "Claims Operations"
    assert model.extraction["extractor"] == "docling" and not model.extraction["ocr_used"]

    headings = [(e.text, e.provenance.section_path) for e in model.elements if e.type == "heading"]
    assert ("1.1 Required documents", ["1 Intake"]) in headings

    [listing] = [e for e in model.elements if e.type == "list"]
    assert listing.attributes["items"] == ["- Completed claim form", "- Photographs of the damage",
                                           "- Police report for theft"]

    tables = [c for c in ctx.chunks if c.chunk_type == "table"]
    assert len(tables) > 1, "40 rows at a 200-token budget must split"
    header = tables[0].text.split("\n")[:2]
    assert header[0].replace(" ", "") == "|Grade|Limit(GBP)|Countersign|"
    rows = []
    for table in tables:
        assert table.text.split("\n")[:2] == header        # the header row, repeated
        assert table.heading_path == ["2 Settlement authority"]
        assert table.token_count <= 200
        rows.extend(table.text.split("\n")[2:])
    assert len(rows) == 40 and rows[0].startswith("| G1 ") and rows[-1].startswith("| G40 ")


async def test_powerpoint_is_one_chunk_per_slide_with_its_speaker_notes():
    ctx = await run_offline("fraud-training.pptx")
    slides = ctx.chunks
    assert all(c.chunk_type == "slide" for c in slides)
    assert [c.provenance.slide for c in slides] == [1, 2]
    assert slides[0].text.startswith("Fraud indicators")
    assert "Speaker notes: Stress that no single indicator proves fraud." in slides[0].text
    # "Questions" has nothing to say on its own; it rides with slide 2.
    assert slides[1].text.endswith("Questions") and slides[1].metadata["slide_end"] == 3
    assert "Slide: 1" in slides[0].contextualized_text
    assert ctx.model.title == "Fraud indicators"


async def test_html_drops_navigation_and_keeps_its_table_whole():
    ctx = await run_offline("privacy-notice.html")
    text = "\n".join(c.text for c in ctx.chunks)
    assert "Home | Products | Contact" not in text
    [table] = [c for c in ctx.chunks if c.chunk_type == "table"]
    assert table.heading_path == ["Retention"] and "| Claim files | 10 years |" in table.text


async def test_a_text_pdf_keeps_pages_and_drops_running_headers_and_footers():
    ctx = await run_offline("motor-policy.pdf")
    model = ctx.model
    assert model.metadata["page_count"] == 3 and model.title == "Motor policy wording"
    text = "\n".join(e.text for e in model.elements)
    assert "Northwind Insurance - Motor policy wording" not in text    # running header
    assert "Page 2 of 3" not in text                                   # running footer
    claims = next(c for c in ctx.chunks if "seven days" in c.text)
    assert claims.provenance.page == 3 and claims.heading_path == ["Section 3 Claims"]
    assert "Page: 3" in claims.contextualized_text
    assert not model.extraction["ocr_used"] and model.extraction["parse_score"] == 1.0


async def test_a_scanned_pdf_is_read_by_ocr_and_its_confidence_recorded():
    ctx = await run_offline("witness-statement-scan.pdf")
    extraction = ctx.model.extraction
    assert extraction["ocr_used"] and extraction["ocr_engine"] == "rapidocr"
    assert extraction["ocr_confidence"] > 0.8
    text = " ".join(c.text for c in ctx.chunks)
    assert "blue van reverse into the parked car" in text
    assert ctx.chunks[0].provenance.page == 1


async def test_an_image_is_read_by_ocr():
    ctx = await run_offline("witness-statement.png")
    assert ctx.family == "image" and ctx.model.extraction["ocr_used"]
    assert "exchanged details" in " ".join(c.text for c in ctx.chunks)


async def test_a_scan_below_the_confidence_floor_fails_as_low_quality():
    with pytest.raises(PermanentError) as error:
        await run_offline("witness-statement-scan.pdf", settings=offline_settings(min_ocr_confidence=0.9999))
    assert error.value.reason == "low_quality_extraction"


async def test_a_pdf_docling_cannot_open_fails_permanently():
    with pytest.raises(PermanentError) as error:
        await run_offline("policy.pdf")
    assert error.value.reason == "extraction_failed" and "policy.pdf" in str(error.value)


async def test_a_pdf_uploaded_without_its_extension_still_reaches_docling():
    ctx = await run_offline("motor-policy", raw=(FIXTURES / "motor-policy.pdf").read_bytes())
    assert ctx.family == "pdf" and any("seven days" in c.text for c in ctx.chunks)
