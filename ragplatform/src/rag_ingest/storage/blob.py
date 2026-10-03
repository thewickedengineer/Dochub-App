"""Azure Blob access: reads what Dochub's extractor stored, writes DocumentModel JSON."""

from __future__ import annotations

import json

import structlog
from azure.core.exceptions import ResourceExistsError, ResourceNotFoundError
from azure.storage.blob.aio import BlobServiceClient

from rag_ingest.config import Settings

log = structlog.get_logger(__name__)


class BlobNotFound(Exception):
    pass


class BlobStore:
    def __init__(self, settings: Settings) -> None:
        self._service = BlobServiceClient.from_connection_string(
            settings.blob_connection_string.get_secret_value()
        )
        self._object_container = settings.object_container
        self._container_ready = False

    async def read(self, container: str, path: str) -> bytes:
        blob = self._service.get_blob_client(container, path)
        try:
            downloader = await blob.download_blob()
            return await downloader.readall()
        except ResourceNotFoundError as error:
            raise BlobNotFound(f"{container}/{path} does not exist") from error

    async def write_json(self, key: str, payload: dict) -> str:
        """Writes under the RAG platform's own container; returns container/key."""
        if not self._container_ready:
            try:
                await self._service.create_container(self._object_container)
            except ResourceExistsError:
                pass
            self._container_ready = True
        blob = self._service.get_blob_client(self._object_container, key)
        await blob.upload_blob(
            json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            overwrite=True,
            content_type="application/json",
        )
        return f"{self._object_container}/{key}"

    async def read_json(self, key: str) -> dict | None:
        """An object from the platform's container, or None when it isn't there."""
        try:
            downloader = await self._service.get_blob_client(self._object_container, key).download_blob()
            return json.loads(await downloader.readall())
        except ResourceNotFoundError:
            return None

    async def delete_prefix(self, prefix: str) -> int:
        """Deletes every object under a prefix in the platform's container; missing is fine."""
        container = self._service.get_container_client(self._object_container)
        removed = 0
        try:
            async for item in container.list_blobs(name_starts_with=prefix):
                try:
                    await container.delete_blob(item.name)
                    removed += 1
                except ResourceNotFoundError:
                    pass
        except ResourceNotFoundError:
            return 0
        return removed

    async def close(self) -> None:
        await self._service.close()
