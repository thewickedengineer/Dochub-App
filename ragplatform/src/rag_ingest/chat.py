"""
Grounded chat over the index: retrieve with hybrid search, then have the LLM
answer from the numbered sources only, citing them as [n].

Streams events as dicts:
  {"type": "sources", "query": ..., "sources": [...]}   — before any text
  {"type": "delta", "text": ...}                         — answer text as it arrives
  {"type": "done", "cited": [n...], "usage": {...}, "model": ...}

Tenant and ACL scoping come from the request, which only Dochub's API may send
(the endpoint requires the service key); the user never chooses them.
"""

from __future__ import annotations

import re
from typing import Any, AsyncIterator, Literal

from pydantic import BaseModel, Field

from rag_ingest.config import Settings
from rag_ingest.llm.client import LLMClient, LLMResult, LLMUsage
from rag_ingest.llm.embedder import Embedder
from rag_ingest.llm.prompts import CHAT_SYSTEM, REWRITE_SYSTEM
from rag_ingest.search import SearchFilters, SearchHit, SearchRequest, search
from rag_ingest.storage.repository import Repository

NO_LLM = ("No language model is configured for answers yet, so here are the most relevant passages "
          "instead. Set LLM_PROVIDER for the RAG platform to get written answers.")
NOTHING_FOUND = "I couldn't find anything about that in the documents you can access."
_CITATION = re.compile(r"\[(\d{1,2})\]")


class ChatMessage(BaseModel):
    role: Literal["user", "assistant"]
    content: str = Field(min_length=1, max_length=20_000)


class ChatRequest(BaseModel):
    tenant_id: str
    principals: list[str]
    messages: list[ChatMessage] = Field(min_length=1)
    filters: SearchFilters = SearchFilters()
    top_k: int | None = Field(default=None, ge=1, le=20)


def location(hit: SearchHit) -> str | None:
    p = hit.provenance or {}
    if p.get("start_ms") is not None:
        return f"{_clock(p['start_ms'])}–{_clock(p.get('end_ms') or p['start_ms'])}"
    if p.get("slide") is not None:
        return f"slide {p['slide']}"
    if p.get("sheet") or p.get("row_range"):
        rows = p.get("row_range")
        return " ".join(x for x in (f"sheet {p['sheet']}" if p.get("sheet") else "",
                                    f"rows {rows[0]}–{rows[1]}" if rows else "") if x)
    if p.get("page") is not None:
        return f"page {p['page']}"
    if p.get("line_range"):
        first, last = p["line_range"]
        return f"line {first}" if first == last else f"lines {first}–{last}"
    return None


def _clock(ms: int) -> str:
    s = ms // 1000
    return f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60:02d}:{s % 60:02d}"


def source_view(n: int, hit: SearchHit) -> dict[str, Any]:
    return {
        "n": n, "chunk_id": hit.chunk_id, "document_id": hit.document_id,
        "dochub_document_id": hit.dochub_document_id, "artifact_id": hit.artifact_id,
        "title": hit.title, "filename": hit.filename, "heading_path": hit.heading_path,
        "location": location(hit), "provenance": hit.provenance, "chunk_type": hit.chunk_type,
        "snippet": hit.text[:600], "score": hit.score, "rerank_score": hit.rerank_score,
    }


def sources_block(hits: list[SearchHit]) -> str:
    parts = ["<sources>"]
    for n, hit in enumerate(hits, start=1):
        where = " › ".join(hit.heading_path) if hit.heading_path else ""
        attrs = f'n="{n}" document="{hit.title or hit.filename or ""}"'
        if where:
            attrs += f' section="{where}"'
        if location(hit):
            attrs += f' location="{location(hit)}"'
        parts.append(f"<source {attrs}>\n{hit.text}\n</source>")
    parts.append("</sources>")
    return "\n".join(parts)


async def standalone_query(llm: LLMClient | None, messages: list[ChatMessage]) -> tuple[str, LLMUsage]:
    question = messages[-1].content
    if llm is None or len(messages) == 1:
        return question, LLMUsage()
    history = "\n".join(f"{m.role}: {m.content}" for m in messages[-7:])
    result = await llm.complete(purpose="query_rewrite", system=REWRITE_SYSTEM,
                                prompt=f"<conversation>\n{history}\n</conversation>", max_tokens=80)
    return (result.text.strip() or question), result.usage


async def answer(repo: Repository, embedder: Embedder, llm: LLMClient | None, settings: Settings,
                 request: ChatRequest, reranker=None) -> AsyncIterator[dict[str, Any]]:
    messages = request.messages[-(settings.chat_history_turns * 2 + 1):]
    if messages[-1].role != "user":
        raise ValueError("the last message must be the user's")
    usage = LLMUsage()
    query, rewrite_usage = await standalone_query(llm, messages)
    usage.add(rewrite_usage)

    hits = await search(repo, embedder, SearchRequest(
        tenant_id=request.tenant_id, principals=request.principals, query=query,
        top_k=request.top_k or settings.chat_top_k, filters=request.filters),
        reranker=reranker, lexical_match=settings.lexical_match, lexical_weight=settings.lexical_weight)
    yield {"type": "sources", "query": query, "sources": [source_view(n, h) for n, h in enumerate(hits, 1)]}

    if not hits:
        yield {"type": "delta", "text": NOTHING_FOUND}
        yield {"type": "done", "cited": [], "usage": usage.as_dict(), "model": None}
        return
    if llm is None:
        yield {"type": "delta", "text": NO_LLM}
        yield {"type": "done", "cited": [], "usage": usage.as_dict(), "model": None}
        return

    turns = [{"role": m.role, "content": m.content} for m in messages]
    final: LLMResult | None = None
    async for part in llm.stream(purpose="chat", system=CHAT_SYSTEM, messages=turns,
                                 max_tokens=settings.chat_max_tokens, context=sources_block(hits)):
        if isinstance(part, LLMResult):
            final = part
        else:
            yield {"type": "delta", "text": part}
    if final is not None:
        usage.add(final.usage)
    text = final.text if final else ""
    cited = sorted({int(n) for n in _CITATION.findall(text) if 1 <= int(n) <= len(hits)})
    yield {"type": "done", "cited": cited, "usage": usage.as_dict(), "model": final.model if final else None}
