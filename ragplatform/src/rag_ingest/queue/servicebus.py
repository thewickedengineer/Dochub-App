"""Consumer for a real Azure Service Bus queue (QUEUE_BACKEND=servicebus)."""

from __future__ import annotations

import json
from datetime import datetime, timezone

import structlog
from azure.servicebus import ServiceBusMessage, ServiceBusReceiveMode
from azure.servicebus.aio import ServiceBusClient

from rag_ingest.config import Settings
from rag_ingest.queue.base import Lease, backoff_for

log = structlog.get_logger(__name__)


class ServiceBusQueue:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        connection = settings.servicebus_connection_string.get_secret_value()
        if connection:
            self._client = ServiceBusClient.from_connection_string(connection)
        elif settings.servicebus_namespace:
            from azure.identity.aio import DefaultAzureCredential
            self._client = ServiceBusClient(settings.servicebus_namespace, DefaultAzureCredential())
        else:
            raise ValueError("QUEUE_BACKEND=servicebus needs SERVICEBUS_CONNECTION_STRING or SERVICEBUS_NAMESPACE.")
        self._receiver = self._client.get_queue_receiver(
            settings.process_queue, receive_mode=ServiceBusReceiveMode.PEEK_LOCK, max_wait_time=5,
        )
        self._sender = self._client.get_queue_sender(settings.process_queue)

    async def receive(self) -> Lease | None:
        messages = await self._receiver.receive_messages(max_message_count=1, max_wait_time=5)
        if not messages:
            return None
        message = messages[0]
        body = json.loads(b"".join(message.body).decode("utf-8"))
        # A scheduled retry is a new message whose delivery count restarts at 1,
        # so the attempt number travels in a property or retries never run out.
        carried = (message.application_properties or {}).get(b"attempt") \
            or (message.application_properties or {}).get("attempt") or 0
        attempts = max(message.delivery_count or 1, int(carried) + 1)

        async def complete() -> None:
            await self._receiver.complete_message(message)

        async def abandon(reason: str) -> None:
            if attempts >= self._settings.max_attempts:
                await self._receiver.dead_letter_message(message, reason="MaxAttempts", error_description=reason[:1000])
                return
            # Abandon alone redelivers at once and burns the budget in seconds;
            # schedule a copy for later and settle the original, as the API does.
            retry_at = datetime.now(timezone.utc) + backoff_for(attempts)
            copy = ServiceBusMessage(
                json.dumps(body),
                message_id=f"{message.message_id}-retry-{attempts}",
                subject=message.subject,
                content_type="application/json",
                session_id=message.session_id,
                application_properties={"attempt": attempts},
            )
            copy.scheduled_enqueue_time_utc = retry_at
            await self._sender.send_messages(copy)
            await self._receiver.complete_message(message)

        async def dead_letter(reason: str) -> None:
            await self._receiver.dead_letter_message(message, reason="Rejected", error_description=reason[:1000])

        async def renew() -> None:
            await self._receiver.renew_message_lock(message)

        lease = Lease(
            message_id=message.message_id or "",
            subject=message.subject or "",
            body=body,
            delivery_count=attempts,
            complete=complete, abandon=abandon, dead_letter=dead_letter, renew=renew,
        )
        lease._max_attempts = self._settings.max_attempts
        return lease

    async def send(self, body: dict, message_id: str, subject: str) -> None:
        await self._sender.send_messages(ServiceBusMessage(
            json.dumps(body), message_id=message_id, subject=subject, content_type="application/json"))

    async def replay_dead_letters(self, limit: int) -> int:
        """Moves dead-lettered messages back onto the queue with a fresh attempt count."""
        from azure.servicebus import ServiceBusSubQueue
        replayed = 0
        async with self._client.get_queue_receiver(self._settings.process_queue, sub_queue=ServiceBusSubQueue.DEAD_LETTER,
                                                   max_wait_time=5) as dlq:
            while replayed < limit:
                batch = await dlq.receive_messages(max_message_count=min(50, limit - replayed), max_wait_time=5)
                if not batch:
                    break
                for message in batch:
                    await self._sender.send_messages(ServiceBusMessage(
                        b"".join(message.body), message_id=f"{message.message_id}-replay",
                        subject=message.subject, content_type="application/json", session_id=message.session_id))
                    await dlq.complete_message(message)
                    replayed += 1
        return replayed

    async def depth(self) -> dict[str, float]:
        """Needs a connection string (the management API); with managed identity this reports nothing."""
        connection = self._settings.servicebus_connection_string.get_secret_value()
        if not connection:
            return {}
        from azure.servicebus.aio.management import ServiceBusAdministrationClient
        async with ServiceBusAdministrationClient.from_connection_string(connection) as admin:
            props = await admin.get_queue_runtime_properties(self._settings.process_queue)
        return {"pending": props.active_message_count, "scheduled": props.scheduled_message_count,
                "dead_lettered": props.dead_letter_message_count}

    async def close(self) -> None:
        await self._receiver.close()
        await self._sender.close()
        await self._client.close()
