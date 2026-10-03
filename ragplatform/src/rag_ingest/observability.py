"""Structured logs and Prometheus metrics."""

from __future__ import annotations

import logging
import sys

import structlog
from prometheus_client import Counter, Gauge, Histogram

STAGE_DURATION = Histogram(
    "rag_stage_duration_seconds", "Time spent in each pipeline stage", ["stage"],
    buckets=(0.01, 0.05, 0.1, 0.5, 1, 2, 5, 10, 30, 60, 120),
)
STAGE_OUTCOMES = Counter("rag_stage_outcomes_total", "Stage results", ["stage", "outcome"])
DOCUMENTS = Counter("rag_documents_total", "Documents finished", ["status"])
CHUNKS_WRITTEN = Counter("rag_chunks_written_total", "Chunks indexed")
TOKENS_EMBEDDED = Counter("rag_tokens_embedded_total", "Tokens sent to the embedder", ["model"])
BATCHES = Counter("rag_batches_total", "Queue messages settled", ["result"])
IN_FLIGHT = Gauge("rag_documents_in_flight", "Documents currently being processed")
BATCHES_IN_FLIGHT = Gauge("rag_batches_in_flight", "Queue messages (uploads) currently held")
QUEUE_DEPTH = Gauge("rag_queue_messages", "Process-queue messages by state", ["state"])
QUEUE_LAG = Gauge("rag_queue_lag_seconds", "How long the oldest waiting process message has waited")
RERANK_CALLS = Counter("rag_rerank_calls_total", "Reranker calls", ["model", "outcome"])
RERANK_DURATION = Histogram("rag_rerank_duration_seconds", "Reranker latency", ["model"],
                            buckets=(0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10))
LLM_CALLS = Counter("rag_llm_calls_total", "LLM calls", ["model", "purpose", "outcome"])
LLM_TOKENS = Counter("rag_llm_tokens_total", "LLM tokens", ["model", "kind"])
LLM_COST = Counter("rag_llm_cost_usd_total", "Estimated LLM spend (needs LLM_PRICE_* set)", ["model", "purpose"])


# ── Tracing ───────────────────────────────────────────────────────────────────
# One trace per batch (a Dochub upload), a span per document and per stage. Spans
# are exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise the API is
# a no-op and costs nothing.

from opentelemetry import trace  # noqa: E402

tracer = trace.get_tracer("rag_ingest")


def configure_tracing(service_name: str) -> bool:
    """OTLP/HTTP export when the standard OTEL_* environment says where to. Returns whether it is on."""
    import os
    if not (os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT") or os.environ.get("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")):
        return False
    from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
    from opentelemetry.sdk.resources import Resource
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor

    provider = TracerProvider(resource=Resource.create({
        "service.name": os.environ.get("OTEL_SERVICE_NAME", service_name)}))
    provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
    trace.set_tracer_provider(provider)
    return True


def _add_trace_ids(_, __, event: dict) -> dict:
    """Puts the current trace and span id on every log line, to jump from a log to its trace."""
    context = trace.get_current_span().get_span_context()
    if context.is_valid:
        event["trace_id"] = format(context.trace_id, "032x")
        event["span_id"] = format(context.span_id, "016x")
    return event


def configure_logging(level: str = "INFO", json_output: bool = True) -> None:
    logging.basicConfig(format="%(message)s", stream=sys.stdout, level=level.upper())
    processors = [
        structlog.contextvars.merge_contextvars,
        _add_trace_ids,
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso", utc=True),
        structlog.processors.StackInfoRenderer(),
        structlog.processors.format_exc_info,
    ]
    processors.append(structlog.processors.JSONRenderer() if json_output else structlog.dev.ConsoleRenderer())
    structlog.configure(
        processors=processors,
        wrapper_class=structlog.make_filtering_bound_logger(logging.getLevelName(level.upper())),
        cache_logger_on_first_use=True,
    )
