"""Phase 1: contracts, adapter, ids, queue policy."""

from datetime import timedelta

from rag_ingest.dochub.adapter import fan_out, map_source, message_id
from rag_ingest.ids import chunk_id, document_id
from rag_ingest.models import DochubProcessRequest, IngestMessage
from rag_ingest.queue import backoff_for

PAYLOAD = {
    "sourceDocumentId": "src-1", "reference": "SRC-1001", "artifactId": "art-1",
    "organizationId": "org-1", "artifactName": "UW Manuals", "teamName": "Underwriting",
    "groupName": "Guidelines", "sourceType": "GoogleDrive", "blobContainer": "dochub-documents",
    "blobPrefix": "teams/u/groups/g/artifacts/a/20261001T000000000Z", "documentCount": 2,
    "uploadedAt": "2026-10-01T00:00:00Z",
    "documents": [
        {"documentId": "d-1", "name": "a.md", "relativePath": "docs/a.md", "blobPath": "p/a.md",
         "blobUrl": "http://x/p/a.md", "sizeBytes": 10, "contentType": "text/markdown",
         "checksumSha256": "ab", "revision": 1, "contentMd5": "md5-a", "documentVersionId": "v-1",
         "sourceType": "GoogleDrive", "externalId": "drive-1"},
        {"documentId": "d-2", "name": "b.py", "relativePath": "b.py", "blobPath": "p/b.py",
         "blobUrl": "http://x/p/b.py", "sizeBytes": 20, "revision": 3, "contentMd5": "md5-b",
         "documentVersionId": "v-2", "sourceType": "GoogleDrive"},
    ],
}


def test_dochub_payload_round_trips_from_camel_case():
    request = DochubProcessRequest.model_validate(PAYLOAD)
    assert request.source_document_id == "src-1"
    assert request.documents[1].revision == 3
    again = DochubProcessRequest.model_validate(request.model_dump(by_alias=True, mode="json"))
    assert again == request


def test_fan_out_produces_one_ingest_message_per_document():
    messages = fan_out(DochubProcessRequest.model_validate(PAYLOAD))
    assert len(messages) == 2
    first = messages[0]
    assert isinstance(first, IngestMessage)
    assert first.tenant_id == "org-1"
    assert first.source == "gdrive"
    assert first.source_item_id == "d-1"          # stable across revisions
    assert first.source_version == "md5-a"        # the hash Dochub recorded
    assert first.hints["blob_path"] == "p/a.md"
    assert first.acl.allow == ["org:org-1", "artifact:art-1"]


def test_message_ids_are_version_specific():
    request = DochubProcessRequest.model_validate(PAYLOAD)
    a, b = request.documents
    assert message_id(request, a) == "src-1:d-1:v-1"
    assert message_id(request, a) != message_id(request, b)


def test_document_ids_are_deterministic_and_stable_across_revisions():
    assert document_id("org", "gdrive", "d-1") == document_id("org", "gdrive", "d-1")
    assert document_id("org", "gdrive", "d-1") != document_id("org-2", "gdrive", "d-1")


def test_chunk_ids_change_with_chunker_version():
    assert chunk_id("doc", "markdown:1", 0) == chunk_id("doc", "markdown:1", 0)
    assert chunk_id("doc", "markdown:1", 0) != chunk_id("doc", "markdown:2", 0)


def test_unknown_source_types_map_to_other():
    assert map_source("Jira") == "other"
    assert map_source("Local") == "upload"


def test_backoff_matches_the_dotnet_schedule():
    assert backoff_for(1) == timedelta(seconds=15)
    assert backoff_for(2) == timedelta(seconds=60)
    assert backoff_for(3) == timedelta(seconds=240)
    assert backoff_for(20) == timedelta(hours=1)
