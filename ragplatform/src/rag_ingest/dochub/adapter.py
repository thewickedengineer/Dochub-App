"""
Turns one Dochub process-queue message (a whole submitted source) into the
per-document IngestMessages the pipeline works on.
"""

from __future__ import annotations

from rag_ingest.models import AclInfo, DochubDocument, DochubProcessRequest, IngestMessage, Source

_SOURCES: dict[str, Source] = {
    "googledrive": "gdrive",
    "sharepoint": "sharepoint",
    "github": "github",
    "local": "upload",
    "azuredevops": "other",
    "confluence": "other",
    "jira": "other",
}


def map_source(source_type: str) -> Source:
    return _SOURCES.get(source_type.lower(), "other")


def acl_for(request: DochubProcessRequest) -> AclInfo:
    """
    Dochub authorizes by organization today, so that is the principal every chunk
    carries; the artifact principal is there for the finer-grained scoping to come.
    Search filters on these inside the SQL.
    """
    return AclInfo(allow=[f"org:{request.organization_id}", f"artifact:{request.artifact_id}"])


def message_id(request: DochubProcessRequest, document: DochubDocument) -> str:
    # Version-specific, so a re-upload of the same document is new work while a
    # redelivery of the same message is recognised as already done. A reindex is
    # its own work, with its own run history.
    base = f"{request.source_document_id}:{document.document_id}:{document.document_version_id}"
    return f"{base}:{request.reference}" if request.reindex else base


def _bare(request: DochubProcessRequest, dochub_document_id: str, operation: str) -> IngestMessage:
    """A delete or ACL-only update: no bytes to fetch, just which document."""
    return IngestMessage(
        message_id=f"{request.source_document_id}:{dochub_document_id}:{operation}",
        tenant_id=request.organization_id,
        source=map_source(request.source_type),
        source_item_id=dochub_document_id,
        source_uri="",
        source_version=None,
        operation=operation,
        acl=acl_for(request),
        hints={"dochub_document_id": dochub_document_id, "artifact_id": request.artifact_id,
               "source_document_id": request.source_document_id},
        enqueued_at=request.uploaded_at,
    )


def fan_out(request: DochubProcessRequest) -> list[IngestMessage]:
    acl = acl_for(request)
    bare = [_bare(request, d, "delete") for d in request.removed_document_ids] + \
           [_bare(request, d, "acl_update") for d in request.acl_updated_document_ids]
    return bare + [
        IngestMessage(
            message_id=message_id(request, document),
            tenant_id=request.organization_id,
            source=map_source(document.source_type or request.source_type),
            # Dochub's document id is stable across revisions, so a changed file
            # lands on the same RAG document and replaces its chunks.
            source_item_id=document.document_id,
            source_uri=document.blob_url,
            source_version=document.content_md5,
            operation="upsert",
            acl=acl,
            hints={
                "filename": document.name,
                "relative_path": document.relative_path,
                "content_type": document.content_type or "",
                "blob_container": request.blob_container,
                "blob_path": document.blob_path,
                "content_md5": document.content_md5,
                "checksum_sha256": document.checksum_sha256 or "",
                "size_bytes": str(document.size_bytes),
                "revision": str(document.revision),
                "artifact_id": request.artifact_id,
                "artifact_name": request.artifact_name,
                "team_name": request.team_name,
                "group_name": request.group_name,
                "source_document_id": request.source_document_id,
                "source_reference": request.reference,
                "document_version_id": document.document_version_id,
                "dochub_document_id": document.document_id,
                "external_id": document.external_id or "",
                **({"force": "true"} if request.reindex else {}),
            },
            enqueued_at=request.uploaded_at,
        )
        for document in request.documents
    ]
