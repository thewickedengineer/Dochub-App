"""Queue protocol. Receives are one message at a time, like the extractor's."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from typing import Any, Awaitable, Callable, Protocol

from rag_ingest.config import Settings


def backoff_for(attempts: int) -> timedelta:
    """
    Same schedule as the .NET queue client — 15s, 1m, 4m, 16m, then hourly — so
    a message behaves identically whichever side is holding it.
    """
    seconds = min(3600, 15 * (4 ** max(0, attempts - 1)))
    return timedelta(seconds=seconds)


@dataclass
class Lease:
    message_id: str
    subject: str
    body: dict[str, Any]
    delivery_count: int
    complete: Callable[[], Awaitable[None]]
    abandon: Callable[[str], Awaitable[None]]
    dead_letter: Callable[[str], Awaitable[None]]
    renew: Callable[[], Awaitable[None]]

    @property
    def is_last_attempt(self) -> bool:
        return self._max_attempts is not None and self.delivery_count >= self._max_attempts

    _max_attempts: int | None = None


class Queue(Protocol):
    async def receive(self) -> Lease | None: ...
    async def send(self, body: dict, message_id: str, subject: str) -> None: ...
    async def replay_dead_letters(self, limit: int) -> int: ...
    async def depth(self) -> dict[str, float]: ...
    async def close(self) -> None: ...


def build_queue(settings: Settings) -> Queue:
    if settings.queue_backend == "servicebus":
        from rag_ingest.queue.servicebus import ServiceBusQueue
        return ServiceBusQueue(settings)
    from rag_ingest.queue.postgres import PostgresQueue
    return PostgresQueue(settings)
