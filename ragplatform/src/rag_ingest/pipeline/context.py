"""What travels between stages for one document."""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from typing import Any

from rag_ingest.llm.client import LLMUsage
from rag_ingest.models import Chunk, DocumentModel, IngestMessage


@dataclass
class PipelineContext:
    message: IngestMessage
    document_id: str
    attempt: int = 1
    run_id: uuid.UUID | None = None

    raw: bytes | None = None
    content_hash: str | None = None
    raw_object_key: str = ""
    model_object_key: str | None = None
    mime: str | None = None
    family: str | None = None
    model: DocumentModel | None = None
    chunks: list[Chunk] = field(default_factory=list)
    chunker_name: str = ""
    chunker_version: str = ""
    # The version this document is (or already was) indexed under; see VersionPolicy.
    pipeline_version: str | None = None
    ts_configs: dict[str, str] = field(default_factory=lambda: {"*": "english"})

    summary: str | None = None
    llm_usage: LLMUsage = field(default_factory=LLMUsage)

    stages: list[dict[str, Any]] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    skipped: bool = False
    skip_reason: str | None = None

    def hint(self, name: str, default: str = "") -> str:
        return self.message.hints.get(name) or default

    @property
    def filename(self) -> str:
        return self.hint("filename") or self.hint("relative_path") or self.message.source_item_id

    def document_row(self) -> dict[str, Any]:
        """The documents-table row as it currently stands."""
        acl = self.message.acl
        model = self.model
        return {
            "document_id": self.document_id,
            "tenant_id": self.message.tenant_id,
            "source": self.message.source,
            "source_item_id": self.message.source_item_id,
            "source_uri": self.message.source_uri,
            "source_version": self.message.source_version,
            "content_hash": self.content_hash or "",
            "content_type": self.mime or self.hint("content_type") or "application/octet-stream",
            "content_family": self.family or "text",
            "title": model.title if model else None,
            "language": model.language if model else None,
            "summary": self.summary,
            "metadata": model.metadata if model else {},
            "acl_allow": acl.allow if acl else [],
            "acl_deny": acl.deny if acl else [],
            "raw_object_key": self.raw_object_key,
            "model_object_key": self.model_object_key,
            "artifact_id": self.hint("artifact_id") or None,
            "source_document_id": self.hint("source_document_id") or None,
            "document_version_id": self.hint("document_version_id") or None,
            "filename": self.filename,
        }
