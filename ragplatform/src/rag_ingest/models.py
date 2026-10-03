"""Data contracts (spec section 4). Every stage speaks these types."""

from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

Source = Literal["sharepoint", "gdrive", "onedrive", "github", "upload", "other"]
Operation = Literal["upsert", "delete", "acl_update"]
ContentFamily = Literal[
    "office", "pdf", "spreadsheet", "presentation", "audio_video",
    "code", "markdown", "text", "image", "html",
]
ElementType = Literal[
    "title", "heading", "paragraph", "list", "table", "code",
    "slide", "transcript_segment", "image_caption", "sheet_rows", "other",
]


class AclInfo(BaseModel):
    allow: list[str] = []
    deny: list[str] = []


class IngestMessage(BaseModel):
    """One document to ingest. Claim-check: a pointer and metadata, never bytes."""

    message_id: str
    tenant_id: str
    source: Source
    source_item_id: str
    source_uri: str
    source_version: str | None
    operation: Operation = "upsert"
    acl: AclInfo | None = None
    hints: dict[str, str] = {}
    enqueued_at: datetime


class Provenance(BaseModel):
    page: int | None = None
    section_path: list[str] = []
    slide: int | None = None
    sheet: str | None = None
    row_range: tuple[int, int] | None = None
    start_ms: int | None = None
    end_ms: int | None = None
    speaker: str | None = None
    file_path: str | None = None
    line_range: tuple[int, int] | None = None
    bbox: tuple[float, float, float, float] | None = None


class Element(BaseModel):
    id: str
    type: ElementType
    text: str
    level: int | None = None
    language: str | None = None
    provenance: Provenance = Field(default_factory=Provenance)
    attributes: dict[str, Any] = {}


class DocumentModel(BaseModel):
    document_id: str
    content_hash: str
    content_type: str
    content_family: ContentFamily
    title: str | None = None
    language: str | None = None
    metadata: dict[str, Any] = {}
    elements: list[Element] = []
    extraction: dict[str, Any] = {}


class Chunk(BaseModel):
    chunk_id: str
    document_id: str
    tenant_id: str
    ordinal: int
    text: str
    contextualized_text: str
    token_count: int
    element_ids: list[str]
    provenance: Provenance
    heading_path: list[str]
    chunk_type: str
    metadata: dict[str, Any] = {}
    # Filled by the embed stage.
    embedding: list[float] | None = None


# ── Dochub's process-queue payload ─────────────────────────────────────────────
# Mirrors DocumentsProcessRequestedEvent in the .NET API. One message carries a
# whole submitted source; the adapter fans it out into IngestMessages.

class _Camel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class DochubDocument(_Camel):
    document_id: str
    name: str
    relative_path: str
    blob_path: str
    blob_url: str
    size_bytes: int
    content_type: str | None = None
    checksum_sha256: str | None = None
    revision: int = 1
    content_md5: str
    document_version_id: str
    source_type: str
    external_id: str | None = None


class DochubProcessRequest(_Camel):
    source_document_id: str
    reference: str
    artifact_id: str
    organization_id: str
    artifact_name: str
    team_name: str
    group_name: str
    source_type: str
    blob_container: str
    blob_prefix: str
    document_count: int
    uploaded_at: datetime
    documents: list[DochubDocument]
    # Documents a sync found deleted at the source: removed from the index.
    removed_document_ids: list[str] = []
    # Documents whose permissions changed but whose content did not: ACLs are
    # rewritten without re-embedding (spec 2.7). Dochub does not send these yet.
    acl_updated_document_ids: list[str] = []
    # Set by POST /admin/reindex: rebuild even if unchanged, and don't report to Dochub.
    reindex: bool = False
