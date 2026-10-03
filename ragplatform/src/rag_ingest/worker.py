"""
The worker: takes one submitted source off Dochub's process queue at a time,
runs each of its documents through the pipeline, reports every document back to
Dochub as it finishes, then reports the batch so Dochub can settle the artifact.

Settlement rules, so Dochub always hears how a batch ended:
  • every document succeeded, was skipped, or failed permanently
        → artifact callback, then complete the message
  • some failed transiently and attempts remain
        → abandon with backoff; the redelivery skips what already succeeded
  • the last attempt
        → transient failures are reported as final, artifact callback, complete
"""

from __future__ import annotations

import asyncio
import signal
from dataclasses import dataclass

import structlog
from opentelemetry import trace
from pydantic import ValidationError

from rag_ingest.config import Settings
from rag_ingest.dochub.adapter import fan_out
from rag_ingest.dochub.callbacks import CallbackError, DochubCallbacks
from rag_ingest.ids import document_id as make_document_id
from rag_ingest.llm.client import build_llm
from rag_ingest.llm.embedder import Embedder, build_embedder
from rag_ingest.models import DochubProcessRequest, IngestMessage
from rag_ingest.observability import tracer
from rag_ingest.observability import BATCHES, BATCHES_IN_FLIGHT, DOCUMENTS, IN_FLIGHT, QUEUE_DEPTH, QUEUE_LAG
from rag_ingest.pipeline.build import build_runner
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.runner import Runner
from rag_ingest.queue import Lease, Queue, build_queue
from rag_ingest.storage.blob import BlobStore
from rag_ingest.storage.migrate import migrate
from rag_ingest.storage.repository import Repository

log = structlog.get_logger(__name__)


@dataclass
class DocumentResult:
    message: IngestMessage
    status: str            # succeeded | skipped | failed
    permanent: bool = True
    error: str | None = None
    reported: bool = True  # did Dochub accept the callback?


class Worker:
    def __init__(self, settings: Settings, queue: Queue, repo: Repository, runner: Runner,
                 callbacks: DochubCallbacks, embedder: Embedder, blobs: BlobStore | None = None) -> None:
        self.blobs = blobs
        self.settings = settings
        self.queue = queue
        self.repo = repo
        self.runner = runner
        self.callbacks = callbacks
        self.embedder = embedder
        self._stop = asyncio.Event()
        # Shared by every batch: the global cap, and one cap per tenant.
        self._slots = asyncio.Semaphore(settings.worker_concurrency)
        self._tenant_slots: dict[str, asyncio.Semaphore] = {}

    def _tenant(self, tenant_id: str) -> asyncio.Semaphore | None:
        if self.settings.tenant_concurrency <= 0:
            return None
        if tenant_id not in self._tenant_slots:
            self._tenant_slots[tenant_id] = asyncio.Semaphore(self.settings.tenant_concurrency)
        return self._tenant_slots[tenant_id]

    def stop(self) -> None:
        self._stop.set()

    async def run_forever(self) -> None:
        log.info("worker.started", queue=self.settings.process_queue, backend=self.settings.queue_backend,
                 pipeline_version=self.runner.pipeline_version,
                 embedder=self.embedder.model, dim=self.embedder.dim, concurrency=self.settings.worker_concurrency,
                 batches=self.settings.worker_batch_concurrency, per_tenant=self.settings.tenant_concurrency)
        running: set[asyncio.Task] = set()
        monitor = asyncio.create_task(self._watch_queue())
        try:
            await self._pull(running)
        finally:
            monitor.cancel()
            # A graceful stop: finish what is held rather than abandon it mid-document.
            if running:
                log.info("worker.draining", batches=len(running))
                await asyncio.gather(*running, return_exceptions=True)
        log.info("worker.stopped")

    async def _pull(self, running: set[asyncio.Task]) -> None:
        while not self._stop.is_set():
            if len(running) >= self.settings.worker_batch_concurrency:
                # Back-pressure: hold off the queue until a batch finishes.
                await asyncio.wait(running, return_when=asyncio.FIRST_COMPLETED)
                continue
            try:
                lease = await self.queue.receive()
            except Exception:  # noqa: BLE001 — the queue being briefly unreachable is not fatal
                log.exception("queue.receive_failed")
                await asyncio.sleep(self.settings.queue_idle_poll_seconds * 5)
                continue
            if lease is None:
                try:
                    await asyncio.wait_for(self._stop.wait(), timeout=self.settings.queue_idle_poll_seconds)
                except TimeoutError:
                    pass
                continue
            task = asyncio.create_task(self._handle_counted(lease))
            running.add(task)
            task.add_done_callback(running.discard)

    async def _handle_counted(self, lease: Lease) -> None:
        BATCHES_IN_FLIGHT.inc()
        try:
            with tracer.start_as_current_span("batch", attributes={
                    "messaging.message.id": lease.message_id, "messaging.destination.name": self.settings.process_queue,
                    "rag.attempt": lease.delivery_count}):
                await self.handle(lease)
        except Exception:  # noqa: BLE001 — one batch failing must not stop the worker
            log.exception("batch.crashed", message_id=lease.message_id)
            try:
                await lease.abandon("worker error while handling the batch")
            except Exception:  # noqa: BLE001
                pass
        finally:
            BATCHES_IN_FLIGHT.dec()

    async def _watch_queue(self) -> None:
        """Queue lag and dead-letter depth, for the metrics endpoint."""
        while True:
            try:
                depth = await self.queue.depth()
                for state, value in depth.items():
                    if state == "oldest_waiting_seconds":
                        QUEUE_LAG.set(value)
                    else:
                        QUEUE_DEPTH.labels(state).set(value)
            except Exception:  # noqa: BLE001 — metrics are best effort
                pass
            await asyncio.sleep(30)

    async def handle(self, lease: Lease) -> None:
        try:
            request = DochubProcessRequest.model_validate(lease.body)
        except ValidationError as error:
            # Will never parse, however often it is redelivered.
            await lease.dead_letter(f"unreadable process request: {error}")
            BATCHES.labels("dead_lettered").inc()
            return

        bound = log.bind(reference=request.reference, source_document_id=request.source_document_id,
                         artifact_id=request.artifact_id, attempt=lease.delivery_count)
        bound.info("batch.received", documents=len(request.documents))

        renewer = asyncio.create_task(self._keep_lock(lease))
        try:
            final = lease.is_last_attempt
            tenant = self._tenant(request.organization_id)

            async def one(message: IngestMessage) -> DocumentResult:
                # Tenant first, then global: a tenant at its cap never sits on a global slot.
                if tenant is None:
                    async with self._slots:
                        return await self.process(message, lease.delivery_count, final, request)
                async with tenant, self._slots:
                    return await self.process(message, lease.delivery_count, final, request)

            results = await asyncio.gather(*(one(m) for m in fan_out(request)))

            retry = [r for r in results if (r.status == "failed" and not r.permanent) or not r.reported]
            if retry and not final:
                reason = "; ".join(f"{r.message.hints.get('filename')}: {r.error or 'callback not accepted'}"
                                   for r in retry)[:1500]
                await lease.abandon(reason)
                BATCHES.labels("retried").inc()
                bound.warning("batch.retrying", pending=len(retry))
                return

            if request.reindex:
                # An operator's rebuild of what Dochub already has: nothing to report.
                await lease.complete()
                BATCHES.labels("reindexed").inc()
                bound.info("batch.reindexed", succeeded=sum(r.status in ("succeeded", "skipped") for r in results),
                           failed=sum(r.status == "failed" for r in results))
                return

            try:
                outcome = await self.callbacks.artifact_processed(
                    request.artifact_id, source_document_id=request.source_document_id)
            except (CallbackError, Exception) as error:  # noqa: BLE001
                # Dochub has not heard the batch ended; let it come round again.
                bound.error("batch.artifact_callback_failed", error=str(error))
                await lease.abandon(f"artifact callback failed: {error}")
                BATCHES.labels("retried").inc()
                return

            await lease.complete()
            BATCHES.labels("completed").inc()
            bound.info("batch.completed",
                       succeeded=sum(r.status in ("succeeded", "skipped") for r in results),
                       failed=sum(r.status == "failed" for r in results),
                       artifact_status=outcome.get("artifactStatus"))
        finally:
            renewer.cancel()

    async def process(self, message: IngestMessage, attempt: int, final: bool,
                      request: DochubProcessRequest) -> DocumentResult:
        with tracer.start_as_current_span("document", attributes={
                "rag.tenant_id": message.tenant_id, "rag.operation": message.operation,
                "rag.dochub_document_id": message.source_item_id,
                "rag.filename": message.hints.get("filename", ""), "rag.attempt": attempt}) as span:
            result = await self._process(message, attempt, final, request)
            span.set_attribute("rag.status", result.status)
            if result.status == "failed":
                span.set_status(trace.Status(trace.StatusCode.ERROR, result.error or "failed"))
            return result

    async def _process(self, message: IngestMessage, attempt: int, final: bool,
                       request: DochubProcessRequest) -> DocumentResult:
        if message.operation != "upsert":
            return await self._apply_bare(message)
        document_id = make_document_id(message.tenant_id, message.source, message.source_item_id)
        dochub_id = message.hints["dochub_document_id"]
        version_id = message.hints["document_version_id"]
        bound = log.bind(document_id=document_id, dochub_document_id=dochub_id,
                         message_id=message.message_id, filename=message.hints.get("filename"))

        # A redelivered message whose work already landed: say so again, idempotently —
        # but only while the stored result is what this pipeline would produce now. A
        # changed chunker, embedding model or LLM must re-index, as the fetch stage would.
        done = await self.repo.succeeded_run(message.message_id)
        if done is not None and await self._still_current(document_id):
            reported = await self._report_processed(
                dochub_id, request, version_id, await self.repo.chunk_count(document_id),
                done["content_hash"], skipped=True)
            return DocumentResult(message, "skipped", reported=reported)

        IN_FLIGHT.inc()
        ctx = PipelineContext(message=message, document_id=document_id, attempt=attempt)
        try:
            ctx.run_id = await self.repo.start_run(document_id, message.message_id, "pending", attempt,
                                                   self.runner.pipeline_version)
            outcome = await self.runner.run(ctx)
            await self.repo.finish_run(ctx.run_id, outcome.status, ctx.stages, outcome.error,
                                       outcome.failed_stage, outcome.permanent, ctx.content_hash)
        finally:
            IN_FLIGHT.dec()

        if outcome.status in ("succeeded", "skipped"):
            DOCUMENTS.labels(outcome.status).inc()
            chunk_count = len(ctx.chunks) if outcome.status == "succeeded" else await self.repo.chunk_count(document_id)
            bound.info("document.done", status=outcome.status, chunks=chunk_count, reason=ctx.skip_reason,
                       **({"llm": ctx.llm_usage.as_dict()} if ctx.llm_usage.calls else {}))
            reported = await self._report_processed(dochub_id, request, version_id, chunk_count,
                                                    ctx.content_hash or "", skipped=outcome.status == "skipped",
                                                    pipeline_version=ctx.pipeline_version)
            return DocumentResult(message, outcome.status, reported=reported)

        DOCUMENTS.labels("failed").inc()
        permanent = bool(outcome.permanent) or final
        error = outcome.error or "unknown error"
        if final and not outcome.permanent:
            error = f"gave up after {attempt} attempts: {error}"
        if permanent:
            await self.repo.mark_failed({**ctx.document_row(),
                                         "pipeline_version": self.runner.pipeline_version}, error)
        bound.warning("document.failed", stage=outcome.failed_stage, permanent=permanent, error=error[:300])

        if request.reindex:
            return DocumentResult(message, "failed", permanent=permanent, error=error)
        try:
            await self.callbacks.document_failed(dochub_id, source_document_id=request.source_document_id,
                                                 document_version_id=version_id, error=error,
                                                 stage=outcome.failed_stage, permanent=permanent)
            reported = True
        except Exception as callback_error:  # noqa: BLE001
            bound.error("callback.failed", error=str(callback_error))
            reported = False
        return DocumentResult(message, "failed", permanent=permanent, error=error, reported=reported)

    async def _apply_bare(self, message: IngestMessage) -> DocumentResult:
        """Deletes and ACL-only updates: no fetch, no pipeline, and Dochub already knows."""
        dochub_id = message.hints["dochub_document_id"]
        bound = log.bind(dochub_document_id=dochub_id, message_id=message.message_id, operation=message.operation)
        try:
            if message.operation == "delete":
                outcomes = await self.repo.purge(message.tenant_id, [{"dochub_document_id": dochub_id}])
                for document_id in outcomes[0]["document_ids"]:
                    if self.blobs is not None:
                        await self.blobs.delete_prefix(f"models/{message.tenant_id}/{document_id}/")
                DOCUMENTS.labels("deleted").inc()
                bound.info("document.deleted", outcome=outcomes[0]["outcome"])
            else:
                acl = message.acl
                touched = await self.repo.update_acl(message.tenant_id, dochub_id,
                                                     acl.allow if acl else [], acl.deny if acl else [])
                DOCUMENTS.labels("acl_updated").inc()
                bound.info("document.acl_updated", chunks=touched)
            return DocumentResult(message, "succeeded")
        except Exception as error:  # noqa: BLE001 — retried with the batch
            bound.warning("document.bare_failed", error=str(error))
            return DocumentResult(message, "failed", permanent=False, error=str(error))

    async def _still_current(self, document_id: str) -> bool:
        stored = await self.repo.get_document(document_id)
        if stored is None or stored["status"] != "indexed" or stored["embedding_model"] != self.embedder.model:
            return False
        if self.runner.versions is None:
            return True
        try:
            return stored["pipeline_version"] == self.runner.versions.for_family(stored["content_family"])
        except Exception:  # noqa: BLE001 — no chunker for that family any more
            return False

    async def _report_processed(self, dochub_id: str, request: DochubProcessRequest, version_id: str,
                                chunk_count: int, content_hash: str, skipped: bool,
                                pipeline_version: str | None = None) -> bool:
        if request.reindex:
            return True
        try:
            await self.callbacks.document_processed(
                dochub_id, source_document_id=request.source_document_id, document_version_id=version_id,
                chunk_count=chunk_count, content_sha256=content_hash,
                embedding_model=self.embedder.model,
                pipeline_version=pipeline_version or self.runner.pipeline_version, skipped=skipped)
            return True
        except Exception as error:  # noqa: BLE001
            log.error("callback.failed", dochub_document_id=dochub_id, error=str(error))
            return False

    async def _keep_lock(self, lease: Lease) -> None:
        """A large batch can outlast the lock; renew well before it lapses."""
        interval = max(10.0, self.settings.queue_lock_seconds / 3)
        while True:
            await asyncio.sleep(interval)
            try:
                await lease.renew()
            except Exception:  # noqa: BLE001
                log.exception("queue.renew_failed", message_id=lease.message_id)


async def run(settings: Settings) -> None:
    if settings.worker_metrics_port:
        from prometheus_client import start_http_server
        start_http_server(settings.worker_metrics_port)
        log.info("worker.metrics", url=f"http://0.0.0.0:{settings.worker_metrics_port}/metrics")
    if settings.docling_enabled:
        from rag_ingest.extractors.docling_ext import available
        missing = available()
        if missing:
            # Refuse to start rather than fail every PDF and Office file permanently.
            raise SystemExit(f"Docling is not installed ({missing}), or set DOCLING_ENABLED=false.")
    if settings.media_enabled:
        from rag_ingest.extractors.media import available as media_available
        missing = media_available()
        if missing:
            raise SystemExit(f"Audio/video needs {missing}; install it, or set MEDIA_ENABLED=false.")
    migrate(settings)
    repo = Repository(settings)
    await repo.open()
    blobs = BlobStore(settings)
    embedder = build_embedder(settings)
    callbacks = DochubCallbacks(settings)
    queue = build_queue(settings)
    llm = build_llm(settings)
    worker = Worker(settings, queue, repo, build_runner(settings, blobs, repo, embedder, llm), callbacks, embedder, blobs)

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, worker.stop)
        except NotImplementedError:  # pragma: no cover — Windows
            pass
    try:
        await worker.run_forever()
    finally:
        await queue.close()
        await callbacks.close()
        await blobs.close()
        await repo.close()
