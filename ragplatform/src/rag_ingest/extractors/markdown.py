"""
Markdown (and notebooks): headings build section_path; code fences, tables and
lists are kept as whole elements so a chunker never splits them mid-element.
"""

from __future__ import annotations

import json

from markdown_it import MarkdownIt

from rag_ingest.extractors.base import decode_text
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.pipeline.errors import PermanentError

NAME, VERSION = "markdown", "1"

_md = MarkdownIt("commonmark").enable("table")


def _elements(document_id: str, text: str, start: int, base_path: list[str] | None = None) -> tuple[list[Element], str | None]:
    lines = text.split("\n")
    tokens = _md.parse(text)
    elements: list[Element] = []
    stack: list[tuple[int, str]] = []  # (level, heading text)
    title: str | None = None
    base_path = base_path or []

    def section() -> list[str]:
        return base_path + [h for _, h in stack]

    def add(kind, body, token_map, **extra):
        nonlocal elements
        rng = (token_map[0] + 1, token_map[1]) if token_map else None
        elements.append(Element(
            id=element_id(document_id, start + len(elements)),
            type=kind, text=body,
            provenance=Provenance(section_path=section(), line_range=rng),
            **extra,
        ))

    i = 0
    while i < len(tokens):
        token = tokens[i]
        if token.type == "heading_open":
            level = int(token.tag[1:])
            heading = tokens[i + 1].content.strip()
            while stack and stack[-1][0] >= level:
                stack.pop()
            if title is None and level == 1:
                title = heading
            add("heading", heading, token.map, level=level)
            stack.append((level, heading))
            i += 3
            continue

        if token.type in ("bullet_list_open", "ordered_list_open", "table_open", "blockquote_open"):
            # Take the exact source lines: keeps list markers and table pipes intact.
            close = token.type.replace("_open", "_close")
            j, nest = i + 1, 1
            while j < len(tokens) and nest:
                if tokens[j].type == token.type:
                    nest += 1
                elif tokens[j].type == close:
                    nest -= 1
                j += 1
            body = "\n".join(lines[token.map[0]:token.map[1]]).strip() if token.map else ""
            kind = {"table_open": "table", "blockquote_open": "paragraph"}.get(token.type, "list")
            attrs = {}
            if kind == "table":
                header = body.split("\n", 1)[0]
                attrs = {"header_row": header}
            elif kind == "list":
                attrs = {"items": _list_items(tokens[i:j], lines)}
            if body:
                add(kind, body, token.map, attributes=attrs)
            i = j
            continue

        if token.type in ("fence", "code_block"):
            language = (token.info or "").strip().split(" ")[0] or None
            add("code", token.content.rstrip("\n"), token.map, language=language,
                attributes={"fenced": token.type == "fence"})
        elif token.type == "paragraph_open":
            add("paragraph", tokens[i + 1].content.strip(), token.map)
            i += 3
            continue
        elif token.type == "html_block":
            if token.content.strip():
                add("other", token.content.strip(), token.map)
        i += 1

    return elements, title


def _list_items(tokens, lines: list[str]) -> list[str]:
    """Top-level items as source text, so the chunker can split between them."""
    items, depth = [], 0
    for token in tokens:
        if token.type in ("bullet_list_open", "ordered_list_open"):
            depth += 1
        elif token.type in ("bullet_list_close", "ordered_list_close"):
            depth -= 1
        elif token.type == "list_item_open" and depth == 1 and token.map:
            items.append("\n".join(lines[token.map[0]:token.map[1]]).rstrip())
    return items


def extract(document_id: str, content_hash: str, content_type: str, raw: bytes) -> DocumentModel:
    text = decode_text(raw).replace("\r\n", "\n")
    elements, title = _elements(document_id, text, 0)
    return DocumentModel(
        document_id=document_id, content_hash=content_hash, content_type=content_type,
        content_family="markdown", title=title, elements=elements,
        extraction={"extractor": NAME, "version": VERSION},
    )


def extract_notebook(document_id: str, content_hash: str, content_type: str, raw: bytes) -> DocumentModel:
    import nbformat

    try:
        notebook = nbformat.reads(decode_text(raw), as_version=4)
    except Exception as error:  # noqa: BLE001 — any parse failure means a corrupt notebook
        raise PermanentError("corrupt_notebook", str(error)) from error

    language = (notebook.metadata.get("kernelspec", {}) or {}).get("language") \
        or (notebook.metadata.get("language_info", {}) or {}).get("name")
    elements: list[Element] = []
    title: str | None = None
    for number, cell in enumerate(notebook.cells, start=1):
        source = cell.get("source", "")
        if not source.strip():
            continue
        if cell.cell_type == "markdown":
            found, heading = _elements(document_id, source, len(elements))
            title = title or heading
            for element in found:
                element.attributes["cell"] = number
            elements.extend(found)
        elif cell.cell_type == "code":
            # Outputs are dropped on purpose: they are large and rarely what anyone searches for.
            elements.append(Element(
                id=element_id(document_id, len(elements)),
                type="code", text=source.rstrip(), language=language,
                attributes={"cell": number},
            ))
    return DocumentModel(
        document_id=document_id, content_hash=content_hash, content_type=content_type,
        content_family="markdown", title=title, elements=elements,
        metadata={"notebook_language": language, "cells": len(notebook.cells)},
        extraction={"extractor": "notebook", "version": VERSION},
    )
