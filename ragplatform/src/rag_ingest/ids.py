"""Deterministic identifiers, so re-runs land on the same rows."""

from __future__ import annotations

import hashlib


def _digest(*parts: str) -> str:
    return hashlib.sha256("\x1f".join(parts).encode()).hexdigest()


def document_id(tenant_id: str, source: str, source_item_id: str) -> str:
    return "doc_" + _digest(tenant_id, source, source_item_id)[:32]


def chunk_id(document_id: str, chunker_version: str, ordinal: int) -> str:
    return "chk_" + _digest(document_id, chunker_version, str(ordinal))[:32]


def element_id(document_id: str, ordinal: int) -> str:
    return f"el_{_digest(document_id, str(ordinal))[:16]}"
