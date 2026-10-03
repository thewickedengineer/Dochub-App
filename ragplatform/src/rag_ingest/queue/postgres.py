"""
Consumer for Dochub's Postgres-backed queue (dochub.queue_messages).

This is the local stand-in for Service Bus that the .NET API writes to when
ServiceBus:Enabled=false. It is a real durable queue, not a simulation: claims
use SELECT … FOR UPDATE SKIP LOCKED, locks expire, deliveries are counted and
exhausted messages are dead-lettered. The semantics here must match
DatabaseQueueClient in the API exactly — both sides operate on the same rows.
"""

from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone
from typing import Any

import structlog
from psycopg_pool import AsyncConnectionPool

from rag_ingest.config import Settings
from rag_ingest.queue.base import Lease, backoff_for

log = structlog.get_logger(__name__)

PENDING, IN_FLIGHT, COMPLETED, DEAD_LETTERED = 0, 1, 2, 3


class PostgresQueue:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._queue = settings.process_queue
        self._lock = timedelta(seconds=settings.queue_lock_seconds)
        self._pool = AsyncConnectionPool(settings.dochub_queue_dsn, min_size=1, max_size=4, open=False)

    async def _ensure_open(self) -> None:
        if self._pool.closed:
            await self._pool.open()

    async def receive(self) -> Lease | None:
        await self._ensure_open()
        now = datetime.now(timezone.utc)

        async with self._pool.connection() as conn, conn.transaction():
            row = await (await conn.execute(
                """
                SELECT id, message_id, subject, payload, delivery_count
                FROM dochub.queue_messages
                WHERE queue = %s
                  AND (status = %s OR (status = %s AND locked_until < %s))
                ORDER BY id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
                (self._queue, PENDING, IN_FLIGHT, now),
            )).fetchone()
            if row is None:
                return None

            row_id, message_id, subject, payload, delivery_count = row
            delivery_count += 1
            await conn.execute(
                "UPDATE dochub.queue_messages SET status = %s, delivery_count = %s, locked_until = %s "
                "WHERE id = %s",
                (IN_FLIGHT, delivery_count, now + self._lock, row_id),
            )

        body: dict[str, Any] = payload if isinstance(payload, dict) else json.loads(payload)
        lease = Lease(
            message_id=message_id,
            subject=subject,
            body=body,
            delivery_count=delivery_count,
            complete=lambda: self._complete(row_id),
            abandon=lambda reason: self._abandon(row_id, delivery_count, reason),
            dead_letter=lambda reason: self._dead_letter(row_id, reason),
            renew=lambda: self._renew(row_id),
        )
        lease._max_attempts = self._settings.max_attempts
        return lease

    async def _complete(self, row_id: int) -> None:
        async with self._pool.connection() as conn:
            await conn.execute(
                "UPDATE dochub.queue_messages SET status = %s, completed_at = now(), locked_until = NULL "
                "WHERE id = %s",
                (COMPLETED, row_id),
            )

    async def _abandon(self, row_id: int, attempts: int, reason: str) -> None:
        if attempts >= self._settings.max_attempts:
            await self._dead_letter(row_id, reason)
            return
        # Held invisible for the backoff window rather than released at once.
        retry_at = datetime.now(timezone.utc) + backoff_for(attempts)
        async with self._pool.connection() as conn:
            await conn.execute(
                "UPDATE dochub.queue_messages SET status = %s, locked_until = %s, error = %s WHERE id = %s",
                (IN_FLIGHT, retry_at, reason[:2000], row_id),
            )
        log.warning("queue.abandoned", row_id=row_id, attempts=attempts, retry_at=retry_at.isoformat())

    async def _dead_letter(self, row_id: int, reason: str) -> None:
        async with self._pool.connection() as conn:
            await conn.execute(
                "UPDATE dochub.queue_messages SET status = %s, completed_at = now(), locked_until = NULL, "
                "error = %s WHERE id = %s",
                (DEAD_LETTERED, reason[:2000], row_id),
            )
        log.error("queue.dead_lettered", row_id=row_id, reason=reason[:300])

    async def _renew(self, row_id: int) -> None:
        async with self._pool.connection() as conn:
            await conn.execute(
                "UPDATE dochub.queue_messages SET locked_until = %s WHERE id = %s AND status = %s",
                (datetime.now(timezone.utc) + self._lock, row_id, IN_FLIGHT),
            )

    async def depth(self) -> dict[str, float]:
        """Counts by state, and how long the oldest waiting message has waited (queue lag)."""
        await self._ensure_open()
        async with self._pool.connection() as conn:
            rows = await (await conn.execute(
                "SELECT status, count(*) FROM dochub.queue_messages WHERE queue = %s GROUP BY status",
                (self._queue,),
            )).fetchall()
            oldest = await (await conn.execute(
                "SELECT EXTRACT(EPOCH FROM now() - min(created_at)) FROM dochub.queue_messages "
                "WHERE queue = %s AND status IN (%s, %s)", (self._queue, PENDING, IN_FLIGHT))).fetchone()
        names = {PENDING: "pending", IN_FLIGHT: "in_flight", COMPLETED: "completed", DEAD_LETTERED: "dead_lettered"}
        out: dict[str, float] = {name: 0 for name in names.values()}
        out.update({names.get(status, str(status)): count for status, count in rows})
        out["oldest_waiting_seconds"] = float(oldest[0] or 0)
        return out

    async def send(self, body: dict[str, Any], message_id: str, subject: str) -> None:
        await self._ensure_open()
        async with self._pool.connection() as conn:
            await conn.execute(
                "INSERT INTO dochub.queue_messages (queue, message_id, subject, payload, status, delivery_count, created_at) "
                "VALUES (%s, %s, %s, %s, %s, 0, now()) ON CONFLICT (queue, message_id) DO NOTHING",
                (self._queue, message_id, subject, json.dumps(body), PENDING))

    async def replay_dead_letters(self, limit: int) -> int:
        """Gives dead-lettered messages a fresh set of attempts, oldest first."""
        await self._ensure_open()
        async with self._pool.connection() as conn:
            cur = await conn.execute(
                "UPDATE dochub.queue_messages SET status = %s, delivery_count = 0, locked_until = NULL, "
                "completed_at = NULL, error = left('replayed: ' || coalesce(error, ''), 2000) "
                "WHERE id IN (SELECT id FROM dochub.queue_messages WHERE queue = %s AND status = %s "
                "ORDER BY id LIMIT %s)", (PENDING, self._queue, DEAD_LETTERED, limit))
            return cur.rowcount

    async def close(self) -> None:
        if not self._pool.closed:
            await self._pool.close()
