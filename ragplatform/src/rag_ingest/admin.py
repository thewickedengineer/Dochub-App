"""
Operator actions behind /admin (spec 8): re-index, dead-letter replay, queue state.

A re-index rebuilds what is already in the index — after a chunker, prompt or
embedding change — from the files Dochub stored. It is written as ordinary
process-queue messages marked `reindex`, one per original upload, so the same
worker, retries and dead-lettering apply. The fetch stage then rebuilds even
unchanged content, the extract stage reuses the stored DocumentModel, and Dochub
is not called back: nothing about its records changes.
"""

from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timezone
from typing import Any

from psycopg.rows import dict_row
from pydantic import BaseModel, Field, model_validator

from rag_ingest.queue import Queue
from rag_ingest.storage.repository import Repository

# Inverse of the adapter's mapping, for rebuilding a Dochub-shaped message.
_DOCHUB_SOURCE = {"gdrive": "GoogleDrive", "sharepoint": "SharePoint", "github": "GitHub",
                  "upload": "Local", "other": "Local"}
SUBJECT = "dochub.documents.process.requested"


class ReindexRequest(BaseModel):
    tenant_id: str | None = None
    # Dochub document ids; empty means every document of the tenant.
    document_ids: list[str] = Field(default=[], max_length=10_000)
    # Every tenant — must be asked for explicitly.
    all: bool = False

    @model_validator(mode="after")
    def _scoped(self) -> "ReindexRequest":
        if not self.all and not self.tenant_id:
            raise ValueError("give tenant_id, or all=true to re-index every tenant")
        return self


async def reindex(repo: Repository, queue: Queue, request: ReindexRequest) -> dict[str, Any]:
    where, params = ["raw_object_key <> ''", "source_item_id IS NOT NULL", "deleted_at IS NULL"], []
    if request.tenant_id:
        where.append("tenant_id = %s")
        params.append(request.tenant_id)
    if request.document_ids:
        where.append("source_item_id = ANY(%s)")
        params.append(request.document_ids)
    async with repo.pool.connection() as conn:
        cur = conn.cursor(row_factory=dict_row)
        await cur.execute(
            f"SELECT tenant_id, source, source_item_id, source_version, raw_object_key, content_type, "
            f"artifact_id, source_document_id, document_version_id, metadata FROM documents "
            f"WHERE {' AND '.join(where)}", params)
        rows = await cur.fetchall()

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    batches: dict[tuple, list[dict]] = defaultdict(list)
    skipped = 0
    for row in rows:
        meta = row["metadata"] or {}
        container, _, path = row["raw_object_key"].partition("/")
        if not (path and row["source_version"] and row["document_version_id"] and row["source_document_id"]):
            skipped += 1     # failed before Dochub's identifiers were recorded: nothing to rebuild from
            continue
        batches[(row["tenant_id"], row["source_document_id"], row["artifact_id"] or "")].append({
            "documentId": row["source_item_id"], "name": meta.get("filename") or path.rsplit("/", 1)[-1],
            "relativePath": meta.get("relative_path") or path.rsplit("/", 1)[-1], "blobPath": path, "blobUrl": "",
            "sizeBytes": int(meta.get("size_bytes") or 0), "contentType": row["content_type"],
            "checksumSha256": None, "revision": int(meta.get("revision") or 1),
            "contentMd5": row["source_version"], "documentVersionId": row["document_version_id"],
            "sourceType": _DOCHUB_SOURCE.get(row["source"], "Local"), "externalId": None,
            "_container": container, "_meta": meta,
        })

    sent = 0
    for (tenant, source_document_id, artifact_id), documents in batches.items():
        meta = documents[0].pop("_meta")
        container = documents[0]["_container"]
        for d in documents:
            d.pop("_container", None)
            d.pop("_meta", None)
        reference = f"reindex-{stamp}"
        await queue.send({
            "sourceDocumentId": source_document_id, "reference": reference, "artifactId": artifact_id,
            "organizationId": tenant, "artifactName": meta.get("artifact_name") or "",
            "teamName": meta.get("team_name") or "", "groupName": meta.get("group_name") or "",
            "sourceType": documents[0]["sourceType"], "blobContainer": container, "blobPrefix": "",
            "documentCount": len(documents), "uploadedAt": datetime.now(timezone.utc).isoformat(),
            "documents": documents, "reindex": True,
        }, message_id=f"{reference}-{source_document_id}"[:100], subject=SUBJECT)
        sent += 1
    return {"documents": len(rows) - skipped, "messages": sent, "skipped": skipped, "reference": f"reindex-{stamp}"}
