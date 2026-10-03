"""
Minimal hybrid search to prove the index works (spec section 7): lexical and
semantic lists over the same chunk rows, fused with Reciprocal Rank Fusion.
Tenant and ACL filters run inside the SQL, never after retrieval.
"""

from __future__ import annotations

from datetime import datetime
from typing import Any, Protocol

import numpy as np
from psycopg.rows import dict_row
from pydantic import BaseModel, Field

from rag_ingest.llm.embedder import Embedder
from rag_ingest.storage.repository import Repository

RRF_K = 60
CANDIDATES = 50
# The question's words OR'd together: plainto_tsquery drops stopwords and stems,
# then its AND becomes OR. An empty query (all stopwords) matches nothing.
_ALL_WORDS = "websearch_to_tsquery('{config}', %s)"
_ANY_WORD = "coalesce(nullif(replace(plainto_tsquery('{config}', %s)::text, ' & ', ' | '), '')::tsquery, ''::tsquery)"


class SearchFilters(BaseModel):
    content_family: str | None = None
    artifact_id: str | None = None
    # A team or group scope: Dochub resolves it to the artifacts inside it.
    artifact_ids: list[str] | None = None
    updated_after: datetime | None = None


class SearchRequest(BaseModel):
    tenant_id: str
    principals: list[str]
    query: str = Field(min_length=1)
    top_k: int = Field(default=10, ge=1, le=100)
    filters: SearchFilters = SearchFilters()


class SearchHit(BaseModel):
    chunk_id: str
    document_id: str
    score: float
    lexical_rank: int | None
    semantic_rank: int | None
    text: str
    heading_path: list[str]
    chunk_type: str
    provenance: dict[str, Any]
    title: str | None
    source_uri: str
    artifact_id: str | None
    filename: str | None
    # Dochub's own id for the document, so a citation can link back into Dochub.
    dochub_document_id: str | None = None
    # Set when a reranker scored the hit (0–1, higher is more relevant).
    rerank_score: float | None = None


class Reranker(Protocol):
    candidates: int

    async def rerank(self, query: str, hits: list[SearchHit]) -> list[SearchHit]: ...


class NoRerank:
    candidates = 0

    async def rerank(self, query: str, hits: list[SearchHit]) -> list[SearchHit]:
        return hits


def _rerank_document(hit: SearchHit) -> str:
    """What the reranker reads: where the passage sits, then the passage."""
    where = " › ".join(p for p in [hit.title or hit.filename or "", *hit.heading_path] if p)
    return f"{where}\n{hit.text}" if where else hit.text


class CohereReranker:
    """
    Second-stage scoring with a hosted cross-encoder — Cohere Rerank, or any model
    behind OpenRouter's rerank endpoint, which speaks the same request and response
    (`{model, query, documents, top_n}` → `results[{index, relevance_score}]`).

    A cross-encoder reads the query and
    each candidate together, which the first stage (separate embeddings, word
    matching) cannot. It re-orders the fused candidate pool and can drop the
    passages it scores as irrelevant (RERANK_MIN_SCORE).

    A failure never fails the search: the fused order is returned instead, and
    the failure is counted (rag_rerank_calls_total{outcome}).
    """

    def __init__(self, settings, client=None) -> None:
        import httpx
        self.provider = settings.rerank_provider
        self.model = settings.rerank_model
        self.candidates = settings.rerank_candidates
        self.min_score = settings.rerank_min_score
        self.max_tokens_per_doc = settings.rerank_max_tokens_per_doc
        if self.provider == "openrouter":
            base, key, self._path = settings.openrouter_base_url, settings.openrouter_api_key, "/rerank"
        else:
            base, key, self._path = settings.cohere_base_url, settings.cohere_api_key, "/v2/rerank"
        self._client = client or httpx.AsyncClient(
            base_url=base.rstrip("/"), timeout=settings.rerank_timeout_seconds,
            headers={"Authorization": f"Bearer {key.get_secret_value()}", "Content-Type": "application/json"})

    async def rerank(self, query: str, hits: list[SearchHit]) -> list[SearchHit]:
        import time

        import httpx
        import structlog

        from rag_ingest.observability import RERANK_CALLS, RERANK_DURATION, tracer
        if len(hits) < 2:
            return hits
        body = {"model": self.model, "query": query, "documents": [_rerank_document(h) for h in hits],
                "top_n": len(hits)}
        if self.provider == "cohere":
            body["max_tokens_per_doc"] = self.max_tokens_per_doc
        started = time.perf_counter()
        with tracer.start_as_current_span("rerank", attributes={"rag.rerank.provider": self.provider,
                                                                "rag.rerank.model": self.model,
                                                                "rag.rerank.candidates": len(hits)}) as span:
            for attempt in (1, 2):
                try:
                    response = await self._client.post(self._path, json=body)
                    if response.status_code in (429, 500, 502, 503, 504) and attempt == 1:
                        continue          # one quick retry; search must stay fast
                    response.raise_for_status()
                    results = response.json()["results"]
                    break
                except (httpx.HTTPError, KeyError, ValueError) as error:
                    if attempt == 2 or not isinstance(error, httpx.TransportError):
                        RERANK_CALLS.labels(self.model, "failed").inc()
                        span.set_attribute("rag.rerank.fallback", True)
                        structlog.get_logger(__name__).warning("rerank.failed", error=str(error)[:300])
                        return hits
            else:
                RERANK_CALLS.labels(self.model, "failed").inc()
                return hits
            RERANK_DURATION.labels(self.model).observe(time.perf_counter() - started)
            RERANK_CALLS.labels(self.model, "ok").inc()

        ranked = []
        for result in results:
            hit = hits[result["index"]].model_copy(update={"rerank_score": round(result["relevance_score"], 6)})
            if hit.rerank_score >= self.min_score:
                ranked.append(hit)
        return ranked

    async def close(self) -> None:
        await self._client.aclose()


def build_reranker(settings) -> "CohereReranker | None":
    return CohereReranker(settings) if settings.rerank_provider in ("cohere", "openrouter") else None


def rrf(*ranked: list[str], k: int = RRF_K, weights: list[float] | None = None) -> dict[str, float]:
    scores: dict[str, float] = {}
    for index, ranking in enumerate(ranked):
        weight = weights[index] if weights else 1.0
        for rank, item in enumerate(ranking, start=1):
            scores[item] = scores.get(item, 0.0) + weight / (k + rank)
    return scores


def _filters(request: SearchRequest) -> tuple[str, list[Any]]:
    clauses = ["c.tenant_id = %s", "c.acl_allow && %s::text[]", "NOT (c.acl_deny && %s::text[])"]
    params: list[Any] = [request.tenant_id, request.principals, request.principals]
    if request.filters.content_family:
        clauses.append("c.content_family = %s")
        params.append(request.filters.content_family)
    if request.filters.artifact_id:
        clauses.append("c.artifact_id = %s")
        params.append(request.filters.artifact_id)
    if request.filters.artifact_ids is not None:
        clauses.append("c.artifact_id = ANY(%s)")
        params.append(request.filters.artifact_ids)
    if request.filters.updated_after:
        clauses.append("c.created_at >= %s")
        params.append(request.filters.updated_after)
    return " AND ".join(clauses), params


async def search(repo: Repository, embedder: Embedder, request: SearchRequest,
                 reranker: Reranker | None = None, mode: str = "hybrid",
                 lexical_match: str = "any", lexical_weight: float = 1.0) -> list[SearchHit]:
    """
    `mode` exists for the evaluation harness: "lexical" or "semantic" rank by one
    list alone; "hybrid" (the product) fuses both.
    """
    where, params = _filters(request)
    query_vector = np.asarray(await embedder.embed_query(request.query), dtype=np.float32)

    async with repo.pool.connection() as conn:
        # Both stemmed and unstemmed forms: prose rows are indexed 'english', code 'simple'.
        # Any of the words may match (OR), ranked by how many match and how close
        # together: requiring every word (websearch_to_tsquery's AND) found almost no
        # natural-language questions in the evaluation set.
        lexical = await (await conn.execute(
            f"""
            SELECT c.chunk_id
            FROM chunks c,
                 LATERAL (SELECT {(_ANY_WORD if lexical_match == "any" else _ALL_WORDS).format(config="english")}
                              || {(_ANY_WORD if lexical_match == "any" else _ALL_WORDS).format(config="simple")} q) t
            WHERE {where} AND c.tsv @@ t.q
            ORDER BY ts_rank_cd(c.tsv, t.q, 1) DESC
            LIMIT {CANDIDATES}
            """,
            [request.query, request.query, *params],
        )).fetchall()

        async with conn.transaction():
            # pgvector 0.8: keep scanning the HNSW graph until the filters let
            # enough rows through, instead of returning short under a tenant filter.
            await conn.execute("SET LOCAL hnsw.ef_search = 100")
            await conn.execute("SET LOCAL hnsw.iterative_scan = relaxed_order")
            semantic = await (await conn.execute(
                f"""
                SELECT c.chunk_id
                FROM chunks c
                WHERE {where}
                ORDER BY c.embedding <=> %s
                LIMIT {CANDIDATES}
                """,
                [*params, query_vector],
            )).fetchall()

        lexical_ids = [r[0] for r in lexical]
        semantic_ids = [r[0] for r in semantic]
        if mode == "lexical":
            fused = {c: 1.0 / (RRF_K + i) for i, c in enumerate(lexical_ids, 1)}
        elif mode == "semantic":
            fused = {c: 1.0 / (RRF_K + i) for i, c in enumerate(semantic_ids, 1)}
        else:
            fused = rrf(lexical_ids, semantic_ids, weights=[lexical_weight, 1.0])
        # A reranker re-orders a wider pool than it returns.
        pool = max(request.top_k, getattr(reranker, "candidates", 0) or 0)
        top = sorted(fused, key=fused.__getitem__, reverse=True)[:pool]
        if not top:
            return []

        cur = conn.cursor(row_factory=dict_row)
        await cur.execute(
            """
            SELECT c.chunk_id, c.document_id, c.text, c.heading_path, c.chunk_type, c.provenance,
                   c.artifact_id, d.title, d.source_uri, d.metadata->>'filename' AS filename,
                   d.metadata->>'dochub_document_id' AS dochub_document_id
            FROM chunks c JOIN documents d USING (document_id)
            WHERE c.chunk_id = ANY(%s)
            """,
            (top,),
        )
        rows = {row["chunk_id"]: row for row in await cur.fetchall()}

    hits = [
        SearchHit(
            chunk_id=chunk, document_id=rows[chunk]["document_id"], score=round(fused[chunk], 6),
            lexical_rank=lexical_ids.index(chunk) + 1 if chunk in lexical_ids else None,
            semantic_rank=semantic_ids.index(chunk) + 1 if chunk in semantic_ids else None,
            text=rows[chunk]["text"], heading_path=rows[chunk]["heading_path"],
            chunk_type=rows[chunk]["chunk_type"], provenance=rows[chunk]["provenance"],
            title=rows[chunk]["title"], source_uri=rows[chunk]["source_uri"],
            artifact_id=rows[chunk]["artifact_id"], filename=rows[chunk]["filename"],
            dochub_document_id=rows[chunk]["dochub_document_id"] or None,
        )
        for chunk in top if chunk in rows
    ]
    return (await (reranker or NoRerank()).rerank(request.query, hits))[: request.top_k]
