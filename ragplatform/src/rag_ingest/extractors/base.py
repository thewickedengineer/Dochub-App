"""Shared helpers for extractors."""

from __future__ import annotations

import codecs

from rag_ingest.pipeline.errors import PermanentError


def decode_text(raw: bytes) -> str:
    """Decode with BOM awareness; reject what is clearly not text."""
    for bom, encoding in ((codecs.BOM_UTF8, "utf-8-sig"), (codecs.BOM_UTF16_LE, "utf-16"),
                          (codecs.BOM_UTF16_BE, "utf-16")):
        if raw.startswith(bom):
            return raw.decode(encoding)
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        pass
    if b"\x00" in raw[:8192]:
        raise PermanentError("not_text", "binary content in a text-family file")
    # Legacy single-byte files: cp1252 is the overwhelmingly common case.
    return raw.decode("cp1252", errors="replace")
