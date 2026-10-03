"""
Retrieval evaluation (spec phase 7): does search find the right passage?

    rag-ingest eval                      # configured embedder, no LLM context, all fixtures
    rag-ingest eval --embedding local    # free and offline
    rag-ingest eval --llm on             # with LLM-written chunk context, to measure what it adds
    rag-ingest eval --quick              # skip PDF/scan/audio (no Docling or Whisper models)
    rag-ingest eval --out report.json

It builds a throwaway index from the fixture files, in a database of its own on
the RAG Postgres server, so the numbers depend only on the code and settings
being compared — never on what happens to be in the live index. Then every
question in tests/eval/golden.jsonl is asked in each mode:

    lexical   full-text ranking alone
    semantic  vector ranking alone
    hybrid    both, fused with Reciprocal Rank Fusion (what /search and chat use)
    rerank    hybrid, re-ordered by the configured reranker (when there is one)

A result is relevant when it comes from the expected file and contains the
expected text, so the golden set survives changes to chunk boundaries and ids.

    recall@k  share of the expected passages found in the top k, averaged over questions
    MRR       mean of 1 / rank of the first relevant result (0 when none in the top 20)
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import psycopg
from psycopg.conninfo import conninfo_to_dict, make_conninfo

from rag_ingest.config import Settings, get_settings
from rag_ingest.ids import document_id
from rag_ingest.llm.client import build_llm
from rag_ingest.llm.embedder import build_embedder
from rag_ingest.models import AclInfo, IngestMessage
from rag_ingest.pipeline.build import build_runner
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.runner import Runner
from rag_ingest.search import SearchRequest, build_reranker, search
from rag_ingest.storage.migrate import migrate
from rag_ingest.storage.repository import Repository

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_GOLDEN = ROOT / "tests" / "eval" / "golden.jsonl"
DEFAULT_FIXTURES = ROOT / "tests" / "fixtures"
TENANT = "eval"
HEAVY = {".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".mp3", ".mp4", ".wav", ".m4a", ".mov"}
NOT_CONTENT = {"make_fixtures.py"}
TOP = 20


class MemoryBlobs:
    """The DocumentModel store, in memory: an evaluation run keeps nothing."""

    def __init__(self) -> None:
        self.objects: dict[str, dict] = {}

    async def write_json(self, key: str, payload: dict) -> str:
        self.objects[key] = payload
        return f"memory/{key}"

    async def read_json(self, key: str) -> dict | None:
        return self.objects.get(key)


def load_golden(path: Path) -> list[dict[str, Any]]:
    questions = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    for q in questions:
        if not q.get("query") or not q.get("expect"):
            raise ValueError(f"golden entry {q.get('id')} needs a query and at least one expectation")
    return questions


def relevant(hit, expectation: dict[str, str]) -> bool:
    if (hit.filename or "") != expectation["file"]:
        return False
    needle = expectation.get("contains")
    return needle is None or needle.lower() in hit.text.lower()


def score(hits: list, expect: list[dict[str, str]]) -> dict[str, Any]:
    """Recall at 5 and 20, and the rank of the first relevant result."""
    def found(k: int) -> int:
        return sum(any(relevant(h, e) for h in hits[:k]) for e in expect)

    first = next((i for i, h in enumerate(hits[:TOP], 1) if any(relevant(h, e) for e in expect)), None)
    return {"recall@5": found(5) / len(expect), "recall@20": found(TOP) / len(expect),
            "rr": 1.0 / first if first else 0.0, "first_rank": first}


def summarise(rows: list[dict[str, Any]]) -> dict[str, float]:
    n = len(rows) or 1
    return {"recall@5": sum(r["recall@5"] for r in rows) / n, "recall@20": sum(r["recall@20"] for r in rows) / n,
            "mrr": sum(r["rr"] for r in rows) / n, "questions": len(rows)}


# ── a throwaway index ─────────────────────────────────────────────────────────

def _admin_dsn(dsn: str) -> str:
    return make_conninfo(**{**conninfo_to_dict(dsn), "dbname": "postgres"})


def create_database(settings: Settings) -> str:
    name = f"rag_eval_{uuid.uuid4().hex[:10]}"
    with psycopg.connect(_admin_dsn(settings.rag_database_dsn), autocommit=True) as conn:
        conn.execute(f'CREATE DATABASE "{name}"')
    return make_conninfo(**{**conninfo_to_dict(settings.rag_database_dsn), "dbname": name})


def drop_database(settings: Settings, dsn: str) -> None:
    name = conninfo_to_dict(dsn)["dbname"]
    with psycopg.connect(_admin_dsn(settings.rag_database_dsn), autocommit=True) as conn:
        conn.execute(f'DROP DATABASE IF EXISTS "{name}" WITH (FORCE)')


async def build_index(settings: Settings, fixtures: Path, quick: bool, llm_on: bool, log) -> tuple[Repository, Any, dict]:
    migrate(settings)
    repo = Repository(settings)
    await repo.open()
    embedder = build_embedder(settings)
    llm = build_llm(settings) if llm_on else None
    runner = build_runner(settings, MemoryBlobs(), repo, embedder, llm)
    # The fetch stage reads Dochub's blob storage; here the bytes are already in hand.
    from_detect = Runner(runner.stages[1:], runner.pipeline_version)

    report: dict[str, Any] = {"indexed": [], "failed": {}, "skipped": []}
    files = sorted(p for p in fixtures.iterdir() if p.is_file() and p.name not in NOT_CONTENT)
    for path in files:
        if quick and path.suffix.lower() in HEAVY:
            report["skipped"].append(path.name)
            continue
        raw = path.read_bytes()
        message = IngestMessage(
            message_id=f"eval:{path.name}", tenant_id=TENANT, source="upload", source_item_id=path.name,
            source_uri=f"file://{path}", source_version=None, enqueued_at=datetime.now(timezone.utc),
            acl=AclInfo(allow=[f"org:{TENANT}"]),
            hints={"filename": path.name, "relative_path": path.name, "artifact_name": "Evaluation fixtures"},
        )
        ctx = PipelineContext(message=message, document_id=document_id(TENANT, "upload", path.name))
        ctx.raw, ctx.content_hash = raw, hashlib.sha256(raw).hexdigest()
        started = time.perf_counter()
        outcome = await from_detect.run(ctx)
        if outcome.status == "succeeded":
            report["indexed"].append(path.name)
            log(f"  indexed {path.name:30} {len(ctx.chunks):3} chunks  {time.perf_counter() - started:5.1f}s")
        else:
            report["failed"][path.name] = outcome.error
            log(f"  failed  {path.name:30} {outcome.error}")
    return repo, embedder, report


# ── the run ───────────────────────────────────────────────────────────────────

async def evaluate(settings: Settings, golden: list[dict], fixtures: Path, quick: bool, llm_on: bool,
                   keep: bool, log=print, rerank_per_minute: float = 0) -> dict[str, Any]:
    dsn = create_database(settings)
    settings = settings.model_copy(update={"rag_database_dsn": dsn})
    log(f"Index: {conninfo_to_dict(dsn)['dbname']} · embeddings {settings.embedding_model} ({settings.embedding_dim})"
        f" · LLM context {'on' if llm_on else 'off'}")
    repo = None
    try:
        repo, embedder, index = await build_index(settings, fixtures, quick, llm_on, log)
        available = set(index["indexed"])
        # A question about a file that wasn't indexed (--quick) can't be answered; leave it out.
        asked = [q for q in golden if all(e["file"] in available for e in q["expect"])]
        reranker = build_reranker(settings)
        modes = ["lexical", "semantic", "hybrid"] + (["rerank"] if reranker else [])

        per_mode: dict[str, list[dict]] = {m: [] for m in modes}
        fallbacks: list[str] = []
        last_rerank = 0.0
        for q in asked:
            for mode in modes:
                if mode == "rerank" and rerank_per_minute:
                    # Stay under the provider's rate limit (Cohere trial keys: 10 a minute).
                    wait = 60.0 / rerank_per_minute - (time.monotonic() - last_rerank)
                    if wait > 0:
                        await asyncio.sleep(wait)
                    last_rerank = time.monotonic()
                request = SearchRequest(tenant_id=TENANT, principals=[f"org:{TENANT}"], query=q["query"], top_k=TOP)
                hits = await search(repo, embedder, request,
                                    reranker=reranker if mode == "rerank" else None,
                                    mode="hybrid" if mode == "rerank" else mode,
                                    lexical_match=settings.lexical_match, lexical_weight=settings.lexical_weight)
                if mode == "rerank" and hits and hits[0].rerank_score is None:
                    fallbacks.append(q["id"])     # the reranker failed; this row is just hybrid
                row = score(hits, q["expect"])
                row.update({"id": q["id"], "kind": q.get("kind", ""), "query": q["query"],
                            "top": [f"{h.filename}" for h in hits[:3]]})
                per_mode[mode].append(row)

        summary = {m: summarise(rows) for m, rows in per_mode.items()}
        by_kind = {m: {k: summarise([r for r in rows if r["kind"] == k])
                       for k in sorted({r["kind"] for r in rows})} for m, rows in per_mode.items()}
        return {"settings": {"embedding_model": settings.embedding_model, "embedding_dim": settings.embedding_dim,
                             "lexical_match": settings.lexical_match, "lexical_weight": settings.lexical_weight,
                             "llm_context": llm_on, "reranker": getattr(reranker, "model", None),
                             "pipeline_version": settings.pipeline_version},
                "index": index, "rerank_fallbacks": fallbacks, "questions_asked": len(asked), "questions_skipped": len(golden) - len(asked),
                "summary": summary, "by_kind": by_kind, "per_question": per_mode}
    finally:
        if repo is not None:
            await repo.close()
        if keep:
            log(f"Kept the evaluation index: {dsn}")
        else:
            drop_database(settings, dsn)


def render(result: dict[str, Any]) -> str:
    lines = ["", f"{'mode':10} {'recall@5':>9} {'recall@20':>10} {'MRR':>7}   ({result['questions_asked']} questions"
             + (f", {result['questions_skipped']} skipped" if result["questions_skipped"] else "") + ")"]
    lines.append("-" * 44)
    for mode, s in result["summary"].items():
        lines.append(f"{mode:10} {s['recall@5']:9.3f} {s['recall@20']:10.3f} {s['mrr']:7.3f}")
    lines.append("")
    lines.append("by kind      " + "  ".join(f"{m:>18}" for m in result["by_kind"]))
    kinds = sorted({k for per in result["by_kind"].values() for k in per})
    for kind in kinds:
        cells = []
        for mode in result["by_kind"]:
            s = result["by_kind"][mode].get(kind)
            cells.append(f"R@5 {s['recall@5']:.2f} MRR {s['mrr']:.2f}" if s else " " * 18)
        lines.append(f"{kind:12} " + "  ".join(f"{c:>18}" for c in cells))
    if result.get("rerank_fallbacks"):
        lines.append("")
        lines.append(f"WARNING: the reranker failed for {len(result['rerank_fallbacks'])} question(s) "
                     f"({', '.join(result['rerank_fallbacks'])}); their rerank rows are the hybrid order.")
    best = list(result["summary"])[-1]
    misses = [r for r in result["per_question"][best] if r["recall@5"] < 1.0]
    if misses:
        lines.append("")
        lines.append(f"Not fully found in the top 5 ({best}):")
        for r in misses:
            where = f"first relevant at #{r['first_rank']}" if r["first_rank"] else "not in the top 20"
            lines.append(f"  {r['id']} {r['query'][:52]:52} {where}; top: {', '.join(r['top'])}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="rag-ingest eval", description=__doc__.split("\n\n")[0])
    parser.add_argument("--golden", type=Path, default=DEFAULT_GOLDEN)
    parser.add_argument("--fixtures", type=Path, default=DEFAULT_FIXTURES)
    parser.add_argument("--embedding", choices=["configured", "local"], default="configured")
    parser.add_argument("--llm", choices=["on", "off"], default="off",
                        help="LLM summaries and chunk context while indexing (costs LLM calls)")
    parser.add_argument("--rerank", choices=["configured", "off"], default="configured")
    parser.add_argument("--quick", action="store_true", help="skip PDFs, scans and recordings")
    parser.add_argument("--rerank-per-minute", type=float, default=0,
                        help="pace reranker calls, e.g. 9 for a Cohere trial key (10/min)")
    parser.add_argument("--lexical-match", choices=["any", "all"])
    parser.add_argument("--lexical-weight", type=float)
    parser.add_argument("--keep", action="store_true", help="keep the evaluation database afterwards")
    parser.add_argument("--out", type=Path, help="write the full report as JSON")
    args = parser.parse_args(argv)

    settings = get_settings()
    overrides: dict[str, Any] = {}
    if args.embedding == "local":
        overrides.update(embedding_provider="local", embedding_model="BAAI/bge-small-en-v1.5", embedding_dim=384)
    if args.rerank == "off":
        overrides["rerank_provider"] = "none"
    if args.lexical_match:
        overrides["lexical_match"] = args.lexical_match
    if args.lexical_weight is not None:
        overrides["lexical_weight"] = args.lexical_weight
    if overrides:
        settings = Settings(**{**settings.model_dump(), **overrides})

    result = asyncio.run(evaluate(settings, load_golden(args.golden), args.fixtures, args.quick,
                                  args.llm == "on", args.keep, log=lambda m: print(m, file=sys.stderr),
                                  rerank_per_minute=args.rerank_per_minute))
    print(render(result))
    if args.out:
        args.out.write_text(json.dumps(result, indent=2, default=str))
        print(f"\nFull report: {args.out}")
    return 0
