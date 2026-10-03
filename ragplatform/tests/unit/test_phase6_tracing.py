"""Phase 6: a span per stage under its document, a failed stage marked, trace ids on log lines."""

from datetime import datetime, timezone

import pytest
from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from rag_ingest.models import IngestMessage
from rag_ingest.observability import _add_trace_ids, tracer
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError
from rag_ingest.pipeline.runner import Runner

EXPORTER = InMemorySpanExporter()


@pytest.fixture(scope="module", autouse=True)
def provider():
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(EXPORTER))
    trace.set_tracer_provider(provider)   # once per process; the module tracer is a proxy
    yield


class Ok:
    name, version = "ok", "1"

    async def run(self, ctx):
        return ctx


class Boom:
    name, version = "boom", "1"

    async def run(self, ctx):
        raise PermanentError("bad_input", "nope")


def _ctx() -> PipelineContext:
    message = IngestMessage(message_id="m", tenant_id="t", source="upload", source_item_id="s",
                            source_uri="u", source_version=None, enqueued_at=datetime.now(timezone.utc))
    return PipelineContext(message=message, document_id="doc_1")


async def test_each_stage_is_a_child_span_of_the_document_and_failures_are_marked():
    EXPORTER.clear()
    with tracer.start_as_current_span("document") as parent:
        outcome = await Runner([Ok(), Boom()], "v").run(_ctx())
    assert outcome.status == "failed"

    spans = {s.name: s for s in EXPORTER.get_finished_spans()}
    assert {"document", "stage.ok", "stage.boom"} <= spans.keys()
    for name in ("stage.ok", "stage.boom"):
        assert spans[name].parent.span_id == parent.get_span_context().span_id
        assert spans[name].attributes["rag.document_id"] == "doc_1"
    assert spans["stage.boom"].status.status_code == trace.StatusCode.ERROR
    assert spans["stage.ok"].status.status_code != trace.StatusCode.ERROR


def test_log_lines_carry_the_current_trace_id():
    with tracer.start_as_current_span("x") as span:
        event = _add_trace_ids(None, None, {"event": "hello"})
    assert event["trace_id"] == format(span.get_span_context().trace_id, "032x")
    assert "trace_id" not in _add_trace_ids(None, None, {"event": "outside"})
