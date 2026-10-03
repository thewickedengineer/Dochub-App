"""Phase 2: chunking rules, measured in the embedding tokenizer."""

from pathlib import Path

from rag_ingest.chunkers.base import Budget, merge_pieces, split_recursive
from rag_ingest.chunkers.code_ast import CodeChunker
from rag_ingest.chunkers.structured import StructuredChunker
from rag_ingest.extractors import code, markdown
from rag_ingest.ids import element_id
from rag_ingest.llm.tokenizer import TiktokenTokenizer
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.search import rrf

FIXTURES = Path(__file__).parents[1] / "fixtures"
TOK = TiktokenTokenizer()


def test_recursive_split_respects_max():
    text = " ".join(f"Sentence number {i} talks about reserves." for i in range(200))
    for piece in split_recursive(TOK, text, 50):
        assert TOK.count(piece) <= 50


def test_merge_carries_overlap_only_up_to_budget():
    pieces = [f"Paragraph {i} " + "word " * 30 for i in range(10)]
    chunks = merge_pieces(TOK, pieces, Budget(target=100, max=150, min=20, overlap=40))
    assert len(chunks) > 1
    assert all(TOK.count(c) <= 150 for c in chunks)
    # Overlap: the next chunk begins with the previous chunk's tail.
    assert chunks[1].split("\n\n")[0] in chunks[0]


def test_markdown_chunks_carry_their_heading_path_and_never_split_a_table():
    model = markdown.extract("doc", "h", "text/markdown", (FIXTURES / "README.md").read_bytes())
    chunker = StructuredChunker(TOK, Budget(target=120, max=200, min=20, overlap=20), "markdown", 3)
    chunks = chunker.chunk(model, "t")
    sla = next(c for c in chunks if "24 hours" in c.text)
    assert sla.heading_path == ["Claims Core", "Service levels"]
    tables = [c for c in chunks if "| Line" in c.text]
    assert len(tables) == 1 and "| Commercial |" in tables[0].text
    assert any("```bash" in c.text and "dotnet run" in c.text for c in chunks)
    assert [c.ordinal for c in chunks] == list(range(len(chunks)))


def test_an_oversized_table_repeats_its_header_on_every_part():
    rows = "\n".join(f"| claim {i} | reserve {i * 100} |" for i in range(200))
    table = f"| Claim | Reserve |\n|---|---|\n{rows}"
    model = DocumentModel(document_id="doc", content_hash="h", content_type="text/markdown",
                          content_family="markdown", elements=[
                              Element(id=element_id("doc", 0), type="table", text=table,
                                      attributes={"header_row": "| Claim | Reserve |"})])
    chunks = StructuredChunker(TOK, Budget(100, 150, 20, 0), "markdown", 3).chunk(model, "t")
    assert len(chunks) > 1
    assert all(c.text.startswith("| Claim | Reserve |\n|---|---|") for c in chunks)
    assert all(c.chunk_type == "table" for c in chunks)


def test_code_chunks_one_per_symbol_with_imports_as_context():
    model = code.extract("doc", "h", "text/x-python", (FIXTURES / "rating.py").read_bytes(), "rating.py")
    chunks = CodeChunker(TOK, Budget(300, 400, 20, 0)).chunk(model, "t")
    names = [c.metadata.get("symbol_name") for c in chunks]
    assert "territory_factor" in names and "Rater" in names
    assert all(c.chunk_type == "code_symbol" for c in chunks)
    assert all("import math" not in c.text for c in chunks if c.metadata.get("symbol_name"))
    assert all("import math" in (c.metadata.get("imports") or "") for c in chunks)
    fn = next(c for c in chunks if c.metadata.get("symbol_name") == "territory_factor")
    assert fn.provenance.line_range == (8, 11)


def test_a_large_class_is_split_by_method_with_class_context():
    methods = "\n".join(
        f"    def method_{i}(self):\n        return {i}  # " + "padding " * 40 for i in range(12))
    source = f"class Big:\n    \"\"\"A large class.\"\"\"\n{methods}\n".encode()
    model = code.extract("doc", "h", "text/x-python", source, "big.py")
    chunks = CodeChunker(TOK, Budget(200, 250, 20, 0)).chunk(model, "t")
    method_chunks = [c for c in chunks if c.metadata.get("class_name") == "Big"]
    assert len(method_chunks) == 12
    assert all(c.metadata["class_signature"] == "class Big" for c in method_chunks)
    assert all(c.metadata["class_docstring"] == "A large class." for c in method_chunks)
    assert any(c.metadata.get("part") == "class_overview" for c in chunks)


def test_reciprocal_rank_fusion_rewards_agreement():
    scores = rrf(["a", "b", "c"], ["b", "a", "d"])
    assert max(scores, key=scores.get) in {"a", "b"}
    assert scores["b"] > scores["c"] and scores["a"] > scores["d"]


def test_merging_two_small_sections_cites_their_parent_not_the_second():
    source = (b"# Guide\n\n## Running locally\n\n1. Start it.\n2. Run it.\n\n"
              b"## Escalation\n\nUnacknowledged claims escalate to the duty manager.\n")
    model = markdown.extract("doc", "h", "text/markdown", source)
    chunks = StructuredChunker(TOK, Budget(target=200, max=300, min=40, overlap=0), "markdown", 3).chunk(model, "t")

    [merged] = chunks                                        # both sections fit one chunk
    assert merged.heading_path == ["Guide"]                 # the common parent
    assert merged.text.index("Running locally") < merged.text.index("Start it")
    assert merged.text.index("Escalation") < merged.text.index("duty manager")


def test_a_chunker_version_bump_reindexes_only_its_own_family():
    from rag_ingest.chunkers.registry import ChunkerRegistry
    from rag_ingest.config import Settings
    from rag_ingest.pipeline.build import VersionPolicy

    settings = Settings(_env_file=None, embedding_provider="local", embedding_dim=384)
    registry = ChunkerRegistry(settings, TOK, 512)
    versions = VersionPolicy(settings.pipeline_version, registry, 384)
    code_before, markdown_before = versions.for_family("code"), versions.for_family("markdown")

    registry.for_family("code").version = "999"

    assert versions.for_family("code") != code_before
    assert versions.for_family("markdown") == markdown_before
    assert code_before.startswith(settings.pipeline_version + "+")
