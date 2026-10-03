"""Phase 7: the evaluation's scoring, and the Cohere reranker (mocked HTTP)."""

import json

import httpx
import pytest

from rag_ingest.eval import load_golden, relevant, score, summarise, DEFAULT_GOLDEN
from rag_ingest.search import CohereReranker, SearchHit
from support.offline import offline_settings


def _hit(i: int, filename: str, text: str) -> SearchHit:
    return SearchHit(chunk_id=f"c{i}", document_id=f"d{i}", score=1.0 / (i + 1), lexical_rank=None,
                     semantic_rank=i + 1, text=text, heading_path=["Section"], chunk_type="prose", provenance={},
                     title=filename, source_uri="", artifact_id=None, filename=filename)


# ── scoring ──────────────────────────────────────────────────────────────────

def test_recall_and_reciprocal_rank():
    hits = [_hit(0, "a.md", "nothing"), _hit(1, "b.md", "the 24 hours rule"), _hit(2, "c.md", "x")]
    row = score(hits, [{"file": "b.md", "contains": "24 HOURS"}])
    assert (row["recall@5"], row["first_rank"], row["rr"]) == (1.0, 2, 0.5)
    miss = score(hits, [{"file": "b.md", "contains": "seven days"}])
    assert (miss["recall@20"], miss["rr"], miss["first_rank"]) == (0.0, 0.0, None)
    two = score(hits, [{"file": "b.md"}, {"file": "z.md"}])
    assert two["recall@5"] == 0.5
    assert summarise([row, miss])["mrr"] == 0.25


def test_relevance_needs_the_right_file_and_text():
    assert relevant(_hit(0, "rating.py", "def territory_factor"), {"file": "rating.py", "contains": "territory"})
    assert not relevant(_hit(0, "other.py", "def territory_factor"), {"file": "rating.py", "contains": "territory"})


def test_the_golden_set_is_well_formed():
    questions = load_golden(DEFAULT_GOLDEN)
    assert len(questions) >= 20 and len({q["id"] for q in questions}) == len(questions)
    assert {q["kind"] for q in questions} == {"identifier", "paraphrase"}


# ── reranker ─────────────────────────────────────────────────────────────────

def _reranker(handler, **overrides) -> CohereReranker:
    settings = offline_settings(rerank_provider="cohere", cohere_api_key="k", **overrides)
    client = httpx.AsyncClient(base_url="https://rerank.test/v2", transport=httpx.MockTransport(handler))
    return CohereReranker(settings, client=client)


HITS = [_hit(0, "a.md", "claims go to the duty manager"), _hit(1, "b.md", "fraud goes to the SIU"),
        _hit(2, "c.md", "unrelated")]


async def test_reranking_reorders_and_reads_where_each_passage_sits():
    seen = {}

    def handler(request):
        seen.update(json.loads(request.content))
        return httpx.Response(200, json={"results": [
            {"index": 1, "relevance_score": 0.91}, {"index": 0, "relevance_score": 0.12},
            {"index": 2, "relevance_score": 0.01}]})

    ranked = await _reranker(handler).rerank("where does fraud go", HITS)
    assert [h.chunk_id for h in ranked] == ["c1", "c0", "c2"]
    assert ranked[0].rerank_score == 0.91
    assert seen["model"] == "rerank-v3.5" and seen["query"] == "where does fraud go"
    assert seen["documents"][1].startswith("b.md › Section\n")     # title and heading go with the text


async def test_a_minimum_score_narrows_the_results():
    def handler(request):
        return httpx.Response(200, json={"results": [
            {"index": 1, "relevance_score": 0.8}, {"index": 0, "relevance_score": 0.04}, {"index": 2, "relevance_score": 0.0}]})

    ranked = await _reranker(handler, rerank_min_score=0.05).rerank("q", HITS)
    assert [h.chunk_id for h in ranked] == ["c1"]


async def test_a_rate_limit_is_retried_once():
    calls = []

    def handler(request):
        calls.append(1)
        if len(calls) == 1:
            return httpx.Response(429, json={"message": "slow down"})
        return httpx.Response(200, json={"results": [{"index": 2, "relevance_score": 0.7},
                                                     {"index": 0, "relevance_score": 0.2},
                                                     {"index": 1, "relevance_score": 0.1}]})

    ranked = await _reranker(handler).rerank("q", HITS)
    assert len(calls) == 2 and ranked[0].chunk_id == "c2"


@pytest.mark.parametrize("failure", ["status", "transport"])
async def test_a_failing_reranker_never_fails_the_search(failure):
    def handler(request):
        if failure == "transport":
            raise httpx.ConnectError("refused")
        return httpx.Response(503, json={"message": "down"})

    ranked = await _reranker(handler).rerank("q", HITS)
    assert [h.chunk_id for h in ranked] == ["c0", "c1", "c2"]          # the first-stage order, intact
    assert all(h.rerank_score is None for h in ranked)


def test_cohere_without_a_key_is_refused_at_startup():
    with pytest.raises(ValueError, match="COHERE_API_KEY"):
        offline_settings(rerank_provider="cohere")


async def test_openrouter_uses_its_own_endpoint_and_no_cohere_only_fields():
    seen = {}

    def handler(request):
        seen["path"], seen["body"] = request.url.path, json.loads(request.content)
        return httpx.Response(200, json={"results": [{"index": 1, "relevance_score": 0.48},
                                                     {"index": 0, "relevance_score": 0.002},
                                                     {"index": 2, "relevance_score": 0.001}]})

    settings = offline_settings(rerank_provider="openrouter", openrouter_api_key="k",
                                rerank_model="nvidia/llama-nemotron-rerank-vl-1b-v2:free")
    reranker = CohereReranker(settings, client=httpx.AsyncClient(
        base_url="https://openrouter.test/api/v1", transport=httpx.MockTransport(handler)))
    ranked = await reranker.rerank("q", HITS)
    assert seen["path"] == "/api/v1/rerank" and "max_tokens_per_doc" not in seen["body"]
    assert seen["body"]["model"] == "nvidia/llama-nemotron-rerank-vl-1b-v2:free"
    assert ranked[0].chunk_id == "c1"
