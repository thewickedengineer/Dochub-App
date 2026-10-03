"""Stage 3 — produce the canonical DocumentModel and persist it before chunking."""

from __future__ import annotations

import asyncio
import mimetypes
from pathlib import PurePosixPath

from rag_ingest.config import Settings
from rag_ingest.extractors import code, markdown, media, spreadsheet, text
from rag_ingest.extractors.docling_ext import FAMILIES as DOCLING_FAMILIES
from rag_ingest.extractors.docling_ext import DoclingExtractor
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError, TransientError
from rag_ingest.storage.blob import BlobStore

# Docling picks its backend from the stream's name; a file uploaded without the
# right extension still has to reach the right one.
_EXTENSION_FOR = {
    "application/pdf": ".pdf",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": ".docx",
    "application/vnd.openxmlformats-officedocument.presentationml.presentation": ".pptx",
    "application/vnd.oasis.opendocument.text": ".odt",
    "application/vnd.oasis.opendocument.presentation": ".odp",
    "text/html": ".html",
}


_SAME = {".jpeg": ".jpg", ".htm": ".html", ".tif": ".tiff"}


def docling_name(filename: str, mime: str) -> str:
    name = PurePosixPath(filename).name or "document"
    wanted = _EXTENSION_FOR.get(mime) or (mimetypes.guess_extension(mime) if mime.startswith("image/") else None)
    if not wanted:
        return name
    suffix = PurePosixPath(name).suffix.lower()
    if _SAME.get(suffix, suffix) != _SAME.get(wanted, wanted):
        name += wanted
    return name


class ExtractStage:
    name, version = "extract", "2"

    def __init__(self, blobs: BlobStore, settings: Settings, docling: DoclingExtractor | None = None) -> None:
        self.blobs = blobs
        self.settings = settings
        self.docling = docling or DoclingExtractor(settings)
        # Layout, table and OCR models are heavy: bound how many run at once.
        self._docling_slots = asyncio.Semaphore(settings.docling_concurrency)
        self._whisper_slots = asyncio.Semaphore(settings.whisper_concurrency)
        self.blobs_container = settings.object_container

    async def _stored_model(self, key: str, family: str | None):
        """The model already stored for these exact bytes, if this extract stage wrote it."""
        reader = getattr(self.blobs, "read_json", None)
        if reader is None:
            return None
        try:
            stored = await reader(key)
        except Exception:  # noqa: BLE001 — unreadable means extract again, never fail
            return None
        if not stored or stored.get("extraction", {}).get("stage_version") != self.version \
                or stored.get("content_family") != family:
            return None
        from rag_ingest.models import DocumentModel
        try:
            return DocumentModel.model_validate(stored)
        except Exception:  # noqa: BLE001
            return None

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        path = ctx.hint("relative_path") or ctx.filename
        key = f"models/{ctx.message.tenant_id}/{ctx.document_id}/{ctx.content_hash}.json"

        # Spec 2.1: the DocumentModel is the source of truth, so re-chunking or
        # re-embedding the same bytes never repeats extraction (OCR, transcription).
        reused = await self._stored_model(key, ctx.family)
        if reused is not None:
            ctx.model, ctx.model_object_key = reused, f"{self.blobs_container}/{key}"
            ctx.warnings.append("reused the stored DocumentModel; extraction skipped")
            return ctx

        args = (ctx.document_id, ctx.content_hash or "", ctx.mime or "", ctx.raw or b"")

        # Every extractor runs off the event loop: a large file must not stall the
        # other documents of the batch, or the lock renewal that keeps the batch ours.
        if ctx.family == "markdown":
            extract = markdown.extract_notebook if PurePosixPath(path).suffix.lower() == ".ipynb" else markdown.extract
            model = await asyncio.to_thread(extract, *args)
        elif ctx.family == "code":
            model = await asyncio.to_thread(code.extract, *args, path=path)
        elif ctx.family == "text":
            model = await asyncio.to_thread(text.extract, *args)
        elif ctx.family == "spreadsheet":
            model = await asyncio.to_thread(spreadsheet.extract, *args, path, self.settings)
        elif ctx.family in DOCLING_FAMILIES:
            if not self.settings.docling_enabled:
                raise PermanentError("extractor_disabled", f"{ctx.family} extraction is turned off (DOCLING_ENABLED)")
            async with self._docling_slots:
                model = await asyncio.to_thread(
                    self.docling.extract, *args, docling_name(path, ctx.mime or ""), ctx.family)
        elif ctx.family == "audio_video":
            if not self.settings.media_enabled:
                raise PermanentError("extractor_disabled", "audio/video transcription is turned off (MEDIA_ENABLED)")
            async with self._whisper_slots:
                model = await asyncio.to_thread(media.extract, *args, path, self.settings)
        else:
            raise PermanentError("no_extractor", f"no extractor for {ctx.family}")

        model.extraction["stage_version"] = self.version
        ctx.model = model
        # The model is the source of truth: re-chunking or re-embedding must
        # never require extracting again.
        try:
            ctx.model_object_key = await self.blobs.write_json(key, model.model_dump(mode="json"))
        except Exception as error:  # noqa: BLE001
            raise TransientError("model_store_unavailable", f"{type(error).__name__}: {error}") from error
        return ctx
