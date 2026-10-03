"""Plain text: paragraphs split on blank lines."""

from __future__ import annotations

import re

from rag_ingest.extractors.base import decode_text
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance

NAME, VERSION = "text", "1"


def extract(document_id: str, content_hash: str, content_type: str, raw: bytes) -> DocumentModel:
    text = decode_text(raw).replace("\r\n", "\n").replace("\r", "\n")
    elements: list[Element] = []
    line = 1
    for block in re.split(r"\n\s*\n", text):
        stripped = block.strip()
        lines = block.count("\n") + 1
        if stripped:
            elements.append(Element(
                id=element_id(document_id, len(elements)),
                type="paragraph",
                text=stripped,
                provenance=Provenance(line_range=(line, line + lines - 1)),
            ))
        line += lines + 1
    return DocumentModel(
        document_id=document_id, content_hash=content_hash, content_type=content_type,
        content_family="text", elements=elements,
        extraction={"extractor": NAME, "version": VERSION},
    )
