"""Stage 1 — fetch the bytes Dochub's extractor stored, and verify them."""

from __future__ import annotations

import base64
import hashlib

from rag_ingest.config import Settings
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError, TransientError
from rag_ingest.storage.blob import BlobNotFound, BlobStore
from rag_ingest.storage.repository import Repository


class FetchStage:
    name, version = "fetch", "1"

    def __init__(self, settings: Settings, blobs: BlobStore, repo: Repository, versions) -> None:
        self.settings = settings
        self.blobs = blobs
        self.repo = repo
        self.versions = versions

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        container = ctx.hint("blob_container")
        path = ctx.hint("blob_path")
        if not container or not path:
            raise PermanentError("no_blob_reference", "message names no blob to fetch")

        declared = int(ctx.hint("size_bytes", "0") or 0)
        if declared > self.settings.max_file_bytes:
            raise PermanentError("too_large", f"{declared} bytes exceeds MAX_FILE_BYTES={self.settings.max_file_bytes}")

        try:
            raw = await self.blobs.read(container, path)
        except BlobNotFound as error:
            # Dochub's prefixes are per-upload and never overwritten, so a missing
            # blob will not reappear by waiting.
            raise PermanentError("blob_missing", str(error)) from error
        except Exception as error:  # noqa: BLE001
            raise TransientError("blob_unavailable", f"{type(error).__name__}: {error}") from error

        if len(raw) > self.settings.max_file_bytes:
            raise PermanentError("too_large", f"{len(raw)} bytes exceeds MAX_FILE_BYTES")

        # Dochub recorded the MD5 storage returned on upload. Checking it here
        # proves this is exactly the version Dochub thinks it is.
        expected_md5 = ctx.hint("content_md5")
        actual_md5 = base64.b64encode(hashlib.md5(raw).digest()).decode()  # noqa: S324 — integrity, not security
        if expected_md5 and expected_md5 != actual_md5:
            raise PermanentError("content_hash_mismatch",
                                 f"blob MD5 {actual_md5} does not match the {expected_md5} Dochub recorded")

        ctx.raw = raw
        ctx.content_hash = hashlib.sha256(raw).hexdigest()
        # Not copied: the Dochub blob is already immutable and content-addressed by its prefix.
        ctx.raw_object_key = f"{container}/{path}"

        existing = await self.repo.get_document(ctx.document_id)
        # A reindex rebuilds regardless; otherwise unchanged work is skipped.
        if (ctx.hint("force") != "true" and existing is not None and existing["status"] == "indexed"
                and existing["content_hash"] == ctx.content_hash
                and existing["pipeline_version"] == self._current(existing["content_family"])
                and existing["embedding_model"] == self.settings.embedding_model):
            # Same bytes, same pipeline and chunkers, same model: the vectors are already right.
            await self.repo.touch_document(ctx.document_id, {
                "source_version": ctx.message.source_version,
                "source_uri": ctx.message.source_uri,
                "artifact_id": ctx.hint("artifact_id") or None,
                "source_document_id": ctx.hint("source_document_id") or None,
                "document_version_id": ctx.hint("document_version_id") or None,
                "acl_allow": ctx.message.acl.allow if ctx.message.acl else [],
                "acl_deny": ctx.message.acl.deny if ctx.message.acl else [],
            })
            ctx.skipped = True
            ctx.skip_reason = "unchanged"
            ctx.pipeline_version = existing["pipeline_version"]
        return ctx

    def _current(self, family: str) -> str | None:
        # Same bytes means the same family, so the stored family names the chunker that would run.
        try:
            return self.versions.for_family(family)
        except PermanentError:
            return None
