"""Phase 2: detection, extraction, normalization."""

from pathlib import Path

import pytest

from rag_ingest.extractors import code, markdown, text
from rag_ingest.pipeline.errors import PermanentError
from rag_ingest.pipeline.stages.detect import classify
from rag_ingest.pipeline.stages.normalize import clean_code, clean_prose

FIXTURES = Path(__file__).parents[1] / "fixtures"


@pytest.mark.parametrize("name, family", [
    ("README.md", "markdown"), ("rating.py", "code"), ("FnolService.cs", "code"), ("notes.txt", "text"),
])
def test_text_families_follow_the_extension(name, family):
    _, detected = classify(name, (FIXTURES / name).read_bytes())
    assert detected == family


def test_a_pdf_is_recognised_by_content_not_by_name():
    _, family = classify("renamed.txt", (FIXTURES / "policy.pdf").read_bytes())
    assert family == "pdf"


def test_binary_content_is_not_indexable():
    with pytest.raises(PermanentError) as error:
        classify("blob.bin", b"\x00\x01\x02\x03" * 100)
    assert error.value.reason == "not_indexable"


def test_markdown_keeps_tables_lists_and_fences_whole():
    model = markdown.extract("d", "h", "text/markdown", (FIXTURES / "README.md").read_bytes())
    kinds = [e.type for e in model.elements]
    assert model.title == "Claims Core"
    assert "table" in kinds and "list" in kinds and "code" in kinds
    table = next(e for e in model.elements if e.type == "table")
    assert table.text.count("\n") == 3 and table.attributes["header_row"].startswith("| Line")
    fence = next(e for e in model.elements if e.type == "code")
    assert fence.language == "bash"
    sla = next(e for e in model.elements if "24 hours" in e.text and e.type == "paragraph")
    assert sla.provenance.section_path == ["Claims Core", "Service levels"]


def test_python_symbols_and_header():
    model = code.extract("d", "h", "text/x-python", (FIXTURES / "rating.py").read_bytes(), "rating.py")
    by_kind = {e.attributes["symbol_kind"]: e for e in model.elements}
    assert "import math" in by_kind["file_header"].text
    assert '"""Rating helpers' in by_kind["file_header"].text
    assert by_kind["function_definition"].attributes["symbol_name"] == "territory_factor"
    cls = by_kind["class_definition"]
    assert cls.attributes["symbol_name"] == "Rater"
    assert [m["name"] for m in cls.attributes["methods"]] == ["premium", "explain"]
    assert cls.text.startswith("@dataclass")  # the decorator travels with the class


def test_csharp_symbols_inside_a_namespace():
    model = code.extract("d", "h", "text/plain", (FIXTURES / "FnolService.cs").read_bytes(), "FnolService.cs")
    names = {e.attributes["symbol_name"] for e in model.elements}
    assert {"FnolNotifyService", "IClock"} <= names
    service = next(e for e in model.elements if e.attributes["symbol_name"] == "FnolNotifyService")
    assert [m["name"] for m in service.attributes["methods"]] == ["FnolNotifyService", "AcknowledgeAsync"]


def test_text_splits_on_blank_lines():
    model = text.extract("d", "h", "text/plain", (FIXTURES / "notes.txt").read_bytes())
    assert len(model.elements) == 3


def test_prose_cleaning_fixes_hyphenation_and_spacing():
    assert clean_prose("claim-\nhandling   is  ﬁne") == "claimhandling is fine"


def test_code_cleaning_keeps_indentation_and_identifiers():
    source = "def f():\n    return ﬁle  \n"
    assert clean_code(source) == "def f():\n    return ﬁle"   # NFC leaves the ligature alone
