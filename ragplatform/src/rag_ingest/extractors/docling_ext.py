"""
PDF, Word, PowerPoint, HTML and images through Docling (spec stage 3).

Docling gives layout-aware reading order, section headers, tables with their
structure, OCR for scanned pages, and a per-document confidence report. This
module maps its `DoclingDocument` onto our `DocumentModel`:

- section headers build `section_path`; running headers/footers (Docling's
  "furniture" layer) are dropped, speaker notes (its "notes" layer) are kept;
- consecutive list items become one `list` element, so a chunker never splits
  a list mid-item;
- tables are serialised as Markdown with the header row recorded in attributes;
- a presentation becomes one `slide` element per slide, title + body + notes.

Conversion is CPU-bound and blocking: callers run `extract` in a thread.
"""

from __future__ import annotations

import io
import math
import threading
import zipfile
from typing import Any

from defusedxml import ElementTree

from rag_ingest.config import Settings
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.pipeline.errors import PermanentError

NAME, VERSION = "docling", "1"

# Families this extractor serves, and the extension Docling should see so it
# picks the right backend (it routes on the stream's name).
FAMILIES = {"pdf", "office", "presentation", "html", "image"}


def available() -> str | None:
    """None when Docling can be imported, otherwise why not."""
    try:
        import docling  # noqa: F401
        return None
    except ImportError as error:
        return f"{error}. Install it with: pip install -e '.[docs]'"


# One converter per distinct configuration, shared across the process: loading
# the layout and table models is the slow part, and they are read-only.
_CONVERTERS: dict[tuple, Any] = {}
_CONVERTERS_LOCK = threading.Lock()


class DoclingExtractor:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    # ── converter ──────────────────────────────────────────────────────────────

    def _ocr_options(self):
        from docling.datamodel import pipeline_options as po

        engine = {
            "rapidocr": po.RapidOcrOptions, "easyocr": po.EasyOcrOptions,
            "tesseract": po.TesseractCliOcrOptions, "ocrmac": po.OcrMacOptions, "auto": po.OcrAutoOptions,
        }[self.settings.ocr_engine]
        return engine(lang=self.settings.ocr_languages) if self.settings.ocr_languages else engine()

    def converter(self):
        key = (self.settings.ocr_engine, tuple(self.settings.ocr_languages), self.settings.docling_timeout_seconds)
        with _CONVERTERS_LOCK:
            if key not in _CONVERTERS:
                from docling.datamodel.base_models import InputFormat
                from docling.datamodel.pipeline_options import PdfPipelineOptions
                from docling.document_converter import DocumentConverter, ImageFormatOption, PdfFormatOption

                options = PdfPipelineOptions(
                    do_ocr=True,
                    do_table_structure=True,
                    ocr_options=self._ocr_options(),
                    document_timeout=self.settings.docling_timeout_seconds,
                )
                _CONVERTERS[key] = DocumentConverter(
                    allowed_formats=[InputFormat.PDF, InputFormat.IMAGE, InputFormat.DOCX, InputFormat.PPTX,
                                     InputFormat.HTML, InputFormat.ODT, InputFormat.ODP],
                    format_options={
                        InputFormat.PDF: PdfFormatOption(pipeline_options=options),
                        InputFormat.IMAGE: ImageFormatOption(pipeline_options=options),
                    },
                )
            return _CONVERTERS[key]

    # ── extraction ─────────────────────────────────────────────────────────────

    def extract(self, document_id: str, content_hash: str, content_type: str, raw: bytes,
                filename: str, family: str) -> DocumentModel:
        from docling.datamodel.base_models import ConversionStatus, DocumentStream

        try:
            result = self.converter().convert(
                DocumentStream(name=filename, stream=io.BytesIO(raw)),
                raises_on_error=False,
                max_num_pages=self.settings.max_pages,
                max_file_size=self.settings.max_file_bytes,
            )
        except Exception as error:  # noqa: BLE001 — Docling raises plain exceptions for bad input
            raise PermanentError("extraction_failed", f"docling: {type(error).__name__}: {error}") from error

        errors = [f"{e.component_type}: {e.error_message}" for e in (result.errors or [])]
        if result.status not in (ConversionStatus.SUCCESS, ConversionStatus.PARTIAL_SUCCESS):
            reason = "; ".join(errors) or f"conversion {result.status.value}"
            raise PermanentError("extraction_failed", f"docling could not read {filename}: {reason}")

        document = result.document
        elements = (_slides if family == "presentation" else _flow)(document, document_id)

        confidence = result.confidence
        ocr_score = _number(confidence.ocr_score)
        extraction: dict[str, Any] = {
            "extractor": NAME, "version": VERSION, "docling_version": _docling_version(),
            "status": result.status.value,
            "ocr_used": ocr_score is not None,
            "ocr_engine": self.settings.ocr_engine if ocr_score is not None else None,
            "ocr_confidence": ocr_score,
            "layout_score": _number(confidence.layout_score),
            "parse_score": _number(confidence.parse_score),
            "table_score": _number(confidence.table_score),
            "quality_grade": confidence.mean_grade.value,
            "warnings": errors,
        }
        metadata: dict[str, Any] = {"page_count": len(document.pages) or None}
        title = None
        if family in {"office", "presentation"}:
            properties = _core_properties(raw)
            title = properties.pop("title", None)
            metadata.update(properties)
        if not title:
            title = next((e.text for e in elements if e.type == "title"), None)
        if not title and family == "presentation":
            title = next((e.attributes.get("title") for e in elements if e.attributes.get("title")), None)

        return DocumentModel(
            document_id=document_id, content_hash=content_hash, content_type=content_type,
            content_family=family, title=title,
            metadata={k: v for k, v in metadata.items() if v is not None},
            elements=elements, extraction=extraction,
        )


# ── DoclingDocument → elements ────────────────────────────────────────────────

def _items(document):
    from docling_core.types.doc import ContentLayer

    yield from document.iterate_items(
        with_groups=False, included_content_layers={ContentLayer.BODY, ContentLayer.NOTES})


def _provenance(item, section_path: list[str], slide: bool = False) -> Provenance:
    prov = item.prov[0] if getattr(item, "prov", None) else None
    if prov is None:
        return Provenance(section_path=list(section_path))
    page = prov.page_no
    return Provenance(
        section_path=list(section_path),
        page=None if slide else page,
        slide=page if slide else None,
        bbox=tuple(round(v, 1) for v in prov.bbox.as_tuple()) if prov.bbox else None,
    )


def _table(item, document) -> tuple[str, dict[str, Any]]:
    markdown = item.export_to_markdown(document)
    grid = item.data.grid if item.data else []
    header = [cell.text for cell in grid[0]] if grid else []
    attributes: dict[str, Any] = {"header": header, "rows": max(len(grid) - 1, 0),
                                  "columns": len(header)}
    caption = item.caption_text(document)
    if caption:
        attributes["caption"] = caption
    return markdown, attributes


def _flow(document, document_id: str) -> list[Element]:
    """Documents read top to bottom: PDF, Word, HTML, images."""
    from docling_core.types.doc import (
        CodeItem, ContentLayer, ListItem, PictureItem, SectionHeaderItem, TableItem, TextItem, TitleItem,
    )

    elements: list[Element] = []
    stack: list[tuple[int, str]] = []
    pending_list: list[tuple[str, Any]] = []

    def path() -> list[str]:
        return [text for _, text in stack]

    def add(kind: str, text: str, item, **extra) -> None:
        elements.append(Element(id=element_id(document_id, len(elements)), type=kind, text=text,
                                provenance=_provenance(item, path()), **extra))

    def flush_list() -> None:
        if pending_list:
            lines = [text for text, _ in pending_list]
            add("list", "\n".join(lines), pending_list[0][1], attributes={"items": lines})
            pending_list.clear()

    for item, _ in _items(document):
        if isinstance(item, ListItem):
            marker = (item.marker or "1.") if item.enumerated else "-"
            pending_list.append((f"{marker} {item.text}".strip(), item))
            continue
        flush_list()

        if isinstance(item, TitleItem):
            stack.clear()
            add("title", item.text, item, level=0)
        elif isinstance(item, SectionHeaderItem):
            level = max(int(item.level or 1), 1)
            while stack and stack[-1][0] >= level:
                stack.pop()
            add("heading", item.text, item, level=level)
            stack.append((level, item.text))
        elif isinstance(item, TableItem):
            text, attributes = _table(item, document)
            if text.strip():
                add("table", text, item, attributes=attributes)
        elif isinstance(item, CodeItem):
            language = getattr(item.code_language, "value", None)
            add("code", item.text, item, language=None if language in (None, "unknown") else language.lower())
        elif isinstance(item, PictureItem):
            caption = item.caption_text(document)
            if caption:
                add("image_caption", caption, item)
        elif isinstance(item, TextItem):
            if not item.text.strip():
                continue
            label = getattr(item.label, "value", str(item.label))
            kind = "image_caption" if label == "caption" else "paragraph"
            notes = item.content_layer == ContentLayer.NOTES
            add(kind, item.text, item, attributes={"label": label, **({"notes": True} if notes else {})})
    flush_list()
    return elements


def _slides(document, document_id: str) -> list[Element]:
    """One element per slide: its title, its body, then its speaker notes."""
    from docling_core.types.doc import ContentLayer, ListItem, TableItem, TitleItem

    slides: dict[int, dict[str, Any]] = {}
    order: list[int] = []
    for item, _ in _items(document):
        prov = item.prov[0] if getattr(item, "prov", None) else None
        number = prov.page_no if prov else (order[-1] if order else 1)
        if number not in slides:
            slides[number] = {"title": None, "body": [], "notes": [], "item": item}
            order.append(number)
        slide = slides[number]
        if isinstance(item, TableItem):
            slide["body"].append(item.export_to_markdown(document))
        elif item.content_layer == ContentLayer.NOTES:
            slide["notes"].append(item.text)
        elif isinstance(item, TitleItem) and slide["title"] is None:
            slide["title"] = item.text
        elif isinstance(item, ListItem):
            slide["body"].append(f"- {item.text}")
        elif getattr(item, "text", "").strip():
            slide["body"].append(item.text)

    elements: list[Element] = []
    for number in order:
        slide = slides[number]
        parts = [slide["title"]] if slide["title"] else []
        if slide["body"]:
            parts.append("\n".join(slide["body"]))
        if slide["notes"]:
            parts.append("Speaker notes: " + " ".join(slide["notes"]))
        text = "\n\n".join(parts).strip()
        if not text:
            continue
        elements.append(Element(
            id=element_id(document_id, len(elements)), type="slide", text=text,
            provenance=Provenance(slide=number),
            attributes={"title": slide["title"], "has_notes": bool(slide["notes"])},
        ))
    return elements


# ── helpers ───────────────────────────────────────────────────────────────────

def _number(value) -> float | None:
    try:
        value = float(value)
    except (TypeError, ValueError):
        return None
    return None if math.isnan(value) else round(value, 4)


def _docling_version() -> str | None:
    try:
        from importlib.metadata import version
        return version("docling")
    except Exception:  # noqa: BLE001
        return None


_CORE_NS = {
    "dc": "http://purl.org/dc/elements/1.1/",
    "dcterms": "http://purl.org/dc/terms/",
    "cp": "http://schemas.openxmlformats.org/package/2006/metadata/core-properties",
}


def _core_properties(raw: bytes) -> dict[str, str]:
    """Title, author and dates from an OOXML package's docProps/core.xml."""
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as package:
            root = ElementTree.fromstring(package.read("docProps/core.xml"))
    except Exception:  # noqa: BLE001 — not OOXML, no core properties, or hostile XML (defusedxml refuses it)
        return {}
    fields = {"title": "dc:title", "author": "dc:creator", "created": "dcterms:created",
              "modified": "dcterms:modified", "last_modified_by": "cp:lastModifiedBy"}
    out = {}
    for key, tag in fields.items():
        node = root.find(tag, _CORE_NS)
        if node is not None and node.text and node.text.strip():
            out[key] = node.text.strip()
    return out
