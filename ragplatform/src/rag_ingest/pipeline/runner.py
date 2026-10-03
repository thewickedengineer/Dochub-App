"""Runs stages in order, timing each one and recording it on the run."""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Protocol

import structlog
from opentelemetry import trace

from rag_ingest.observability import STAGE_DURATION, STAGE_OUTCOMES, tracer
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import StageError, TransientError

log = structlog.get_logger(__name__)


class Stage(Protocol):
    name: str
    version: str

    async def run(self, ctx: PipelineContext) -> PipelineContext: ...


@dataclass
class Outcome:
    status: str                 # succeeded | skipped | failed
    error: str | None = None
    failed_stage: str | None = None
    permanent: bool | None = None


class Runner:
    def __init__(self, stages: list[Stage], pipeline_version: str, versions=None) -> None:
        self.stages = stages
        self.pipeline_version = pipeline_version
        # The VersionPolicy: what "already indexed" means for each content family.
        self.versions = versions

    async def run(self, ctx: PipelineContext) -> Outcome:
        for stage in self.stages:
            if ctx.skipped:
                break
            started = time.perf_counter()
            before_chunks = len(ctx.chunks)
            span = tracer.start_span(f"stage.{stage.name}", attributes={
                "rag.stage": stage.name, "rag.stage_version": stage.version, "rag.document_id": ctx.document_id})
            try:
                with trace.use_span(span, end_on_exit=True):
                    await stage.run(ctx)
                    span.set_attribute("rag.chunks", len(ctx.chunks))
                    if ctx.skipped:
                        span.set_attribute("rag.skipped", ctx.skip_reason or "")
            except StageError as error:
                span.set_status(trace.Status(trace.StatusCode.ERROR, str(error)))
                self._record(ctx, stage, started, "failed", error=str(error))
                STAGE_OUTCOMES.labels(stage.name, "failed").inc()
                return Outcome("failed", str(error), stage.name, error.permanent)
            except Exception as error:  # noqa: BLE001 — anything unexpected is retried
                wrapped = TransientError("unexpected_error", f"{type(error).__name__}: {error}")
                self._record(ctx, stage, started, "failed", error=str(wrapped))
                STAGE_OUTCOMES.labels(stage.name, "failed").inc()
                log.exception("stage.crashed", stage=stage.name, document_id=ctx.document_id)
                return Outcome("failed", str(wrapped), stage.name, False)

            self._record(ctx, stage, started, "skipped" if ctx.skipped else "ok",
                         chunks=len(ctx.chunks) - before_chunks or len(ctx.chunks))
            STAGE_OUTCOMES.labels(stage.name, "ok").inc()

        return Outcome("skipped" if ctx.skipped else "succeeded")

    @staticmethod
    def _record(ctx: PipelineContext, stage: Stage, started: float, status: str, **extra) -> None:
        elapsed = time.perf_counter() - started
        STAGE_DURATION.labels(stage.name).observe(elapsed)
        ctx.stages.append({
            "stage": stage.name,
            "version": stage.version,
            "status": status,
            "ms": round(elapsed * 1000, 1),
            **({"warnings": list(ctx.warnings)} if ctx.warnings else {}),
            **extra,
        })
