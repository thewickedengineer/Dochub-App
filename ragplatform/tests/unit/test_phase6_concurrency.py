"""Phase 6: batches run in parallel within a global cap, a per-tenant cap and back-pressure."""

import asyncio
from datetime import datetime, timezone

from rag_ingest.queue.base import Lease
from rag_ingest.worker import DocumentResult, Worker
from support.offline import offline_settings


def _request(tenant: str, batch: int, documents: int) -> dict:
    return {
        "sourceDocumentId": f"{tenant}-src-{batch}", "reference": f"SRC-{batch}", "artifactId": f"{tenant}-art",
        "organizationId": tenant, "artifactName": "A", "teamName": "T", "groupName": "G", "sourceType": "Local",
        "blobContainer": "c", "blobPrefix": "p", "documentCount": documents,
        "uploadedAt": datetime.now(timezone.utc).isoformat(),
        "documents": [{
            "documentId": f"{tenant}-{batch}-{i}", "name": f"{i}.txt", "relativePath": f"{i}.txt",
            "blobPath": f"p/{i}.txt", "blobUrl": "", "sizeBytes": 1, "contentMd5": "x",
            "documentVersionId": f"v-{tenant}-{batch}-{i}", "sourceType": "Local",
        } for i in range(documents)],
    }


class FakeQueue:
    def __init__(self, bodies: list[dict]) -> None:
        self.bodies = list(bodies)
        self.completed = 0
        self.max_held = 0
        self.held = 0

    async def receive(self):
        if not self.bodies:
            return None
        self.held += 1
        self.max_held = max(self.max_held, self.held)

        async def complete():
            self.completed += 1
            self.held -= 1

        async def noop(*_):
            return None

        return Lease("m", "s", self.bodies.pop(0), 1, complete, noop, noop, noop)

    async def depth(self):
        return {}


class FakeCallbacks:
    async def artifact_processed(self, *_, **__):
        return {}


class CountingWorker(Worker):
    """The real loop and caps; documents take a moment instead of running the pipeline."""

    def __init__(self, settings, queue) -> None:
        super().__init__(settings, queue, repo=None, runner=None, callbacks=FakeCallbacks(),
                         embedder=type("E", (), {"model": "m", "dim": 1})())
        self.runner = type("R", (), {"pipeline_version": "t"})()
        self.now = 0
        self.peak = 0
        self.per_tenant: dict[str, int] = {}
        self.peak_tenant: dict[str, int] = {}
        self.order: list[str] = []

    async def process(self, message, attempt, final, request):
        tenant = message.tenant_id
        self.now += 1
        self.per_tenant[tenant] = self.per_tenant.get(tenant, 0) + 1
        self.peak = max(self.peak, self.now)
        self.peak_tenant[tenant] = max(self.peak_tenant.get(tenant, 0), self.per_tenant[tenant])
        self.order.append(tenant)
        await asyncio.sleep(0.02)
        self.now -= 1
        self.per_tenant[tenant] -= 1
        return DocumentResult(message, "succeeded")


async def test_caps_hold_and_a_small_tenant_is_not_starved_by_a_backfill():
    settings = offline_settings(worker_concurrency=4, worker_batch_concurrency=2, tenant_concurrency=2,
                                queue_idle_poll_seconds=0.01)
    # A big backfill from one tenant arrives first, then one small upload from another.
    queue = FakeQueue([_request("big", b, 6) for b in range(3)] + [_request("small", 9, 2)])
    worker = CountingWorker(settings, queue)

    runner = asyncio.create_task(worker.run_forever())
    for _ in range(300):
        if queue.completed == 4:
            break
        await asyncio.sleep(0.01)
    worker.stop()
    await asyncio.wait_for(runner, 5)

    assert queue.completed == 4
    assert queue.max_held <= 2                     # back-pressure: never more batches than allowed
    assert worker.peak <= 4                        # global cap
    assert worker.peak_tenant["big"] <= 2          # per-tenant cap
    # The small tenant got going before the backfill finished.
    assert worker.order.index("small") < len(worker.order) - worker.order[::-1].index("big") - 1


async def test_without_a_tenant_cap_one_tenant_can_use_every_slot():
    settings = offline_settings(worker_concurrency=3, worker_batch_concurrency=1, tenant_concurrency=0,
                                queue_idle_poll_seconds=0.01)
    queue = FakeQueue([_request("only", 1, 6)])
    worker = CountingWorker(settings, queue)
    runner = asyncio.create_task(worker.run_forever())
    for _ in range(300):
        if queue.completed == 1:
            break
        await asyncio.sleep(0.01)
    worker.stop()
    await asyncio.wait_for(runner, 5)
    assert worker.peak_tenant["only"] == 3
