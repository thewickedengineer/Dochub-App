"""Reports progress back to the Dochub API."""

from __future__ import annotations

import asyncio
import random
from typing import Any

import httpx
import structlog

from rag_ingest.config import Settings

log = structlog.get_logger(__name__)

SERVICE_KEY_HEADER = "X-Dochub-Service-Key"


class CallbackError(Exception):
    """Dochub refused a callback in a way retrying will not fix."""


class DochubCallbacks:
    def __init__(self, settings: Settings, transport: httpx.AsyncBaseTransport | None = None) -> None:
        key = settings.dochub_service_key.get_secret_value()
        if len(key) < 32:
            raise ValueError(
                "DOCHUB_SERVICE_KEY must be set (32+ characters) and match Ingestion:ServiceKey in the API."
            )
        self._client = httpx.AsyncClient(
            base_url=settings.dochub_api_url.rstrip("/"),
            headers={SERVICE_KEY_HEADER: key},
            timeout=settings.callback_timeout_seconds,
            transport=transport,
        )

    async def document_processed(self, document_id: str, *, source_document_id: str,
                                  document_version_id: str, chunk_count: int, content_sha256: str,
                                  embedding_model: str, pipeline_version: str, skipped: bool) -> None:
        await self._post(f"/api/ingestion/documents/{document_id}/processed", {
            "sourceDocumentId": source_document_id,
            "documentVersionId": document_version_id,
            "chunkCount": chunk_count,
            "contentSha256": content_sha256,
            "embeddingModel": embedding_model,
            "pipelineVersion": pipeline_version,
            "skipped": skipped,
        })

    async def document_failed(self, document_id: str, *, source_document_id: str,
                               document_version_id: str, error: str, stage: str | None,
                               permanent: bool) -> None:
        await self._post(f"/api/ingestion/documents/{document_id}/failed", {
            "sourceDocumentId": source_document_id,
            "documentVersionId": document_version_id,
            "error": error[:1900],
            "stage": stage,
            "permanent": permanent,
        })

    async def artifact_processed(self, artifact_id: str, *, source_document_id: str) -> dict[str, Any]:
        return await self._post(f"/api/ingestion/artifacts/{artifact_id}/processed",
                                {"sourceDocumentId": source_document_id})

    async def _post(self, path: str, body: dict[str, Any], attempts: int = 5) -> dict[str, Any]:
        for attempt in range(1, attempts + 1):
            try:
                response = await self._client.post(path, json=body)
            except (httpx.TransportError, httpx.TimeoutException) as error:
                if attempt == attempts:
                    raise
                await self._sleep(attempt, f"{type(error).__name__}: {error}", path)
                continue

            if response.status_code < 300:
                return response.json() if response.content else {}

            # A newer version superseded this one; its own callback is what counts.
            if response.status_code == 409 and "stale_version" in response.text:
                log.info("callback.stale_version", path=path)
                return {}

            if response.status_code >= 500 or response.status_code == 429:
                if attempt == attempts:
                    raise CallbackError(f"{path} → {response.status_code}: {response.text[:300]}")
                await self._sleep(attempt, f"HTTP {response.status_code}", path)
                continue

            raise CallbackError(f"{path} → {response.status_code}: {response.text[:300]}")
        return {}

    @staticmethod
    async def _sleep(attempt: int, reason: str, path: str) -> None:
        delay = min(30.0, 0.5 * (2 ** attempt)) * (0.5 + random.random())
        log.warning("callback.retry", path=path, attempt=attempt, reason=reason, delay_s=round(delay, 2))
        await asyncio.sleep(delay)

    async def close(self) -> None:
        await self._client.aclose()
