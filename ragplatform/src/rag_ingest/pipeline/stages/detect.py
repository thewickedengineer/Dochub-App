"""Stage 2 — what is this, and is it worth indexing?"""

from __future__ import annotations

import io
import re
import zipfile
from pathlib import PurePosixPath

from rag_ingest.config import Settings
from rag_ingest.extractors.code import EXTENSIONS as CODE_EXTENSIONS
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError

MARKDOWN = {".md", ".markdown", ".mdx", ".rst", ".adoc", ".ipynb"}
TEXT = {".txt", ".text", ".log", ".csv.txt", ""}
LOCK_FILES = {"package-lock.json", "yarn.lock", "pnpm-lock.yaml", "poetry.lock", "Pipfile.lock",
              "Cargo.lock", "composer.lock", "Gemfile.lock", "go.sum", "packages.lock.json"}
VENDORED = re.compile(r"(^|/)(node_modules|dist|build|vendor|bower_components|\.git|__pycache__|\.venv)/")
MINIFIED = re.compile(r"\.min\.(js|css)$|\.bundle\.js$")

# Families with no extractor in this build. Recorded as permanent failures with
# the reason, rather than quietly skipped.
LATER_PHASES: dict[str, str] = {}

# Binary formats nothing here reads. Docling's converters for these shell out to
# LibreOffice, which this service does not ship; say so instead of failing obscurely.
LEGACY_OFFICE = {".doc": ".docx", ".dot": ".docx", ".ppt": ".pptx", ".pps": ".pptx", ".pot": ".pptx",
                 ".rtf": ".docx", ".xls": ".xlsx", ".xlt": ".xlsx", ".ods": ".xlsx"}

_BINARY_MIME = {
    "application/pdf": "pdf",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "office",
    "application/msword": "office",
    "application/vnd.openxmlformats-officedocument.presentationml.presentation": "presentation",
    "application/vnd.ms-powerpoint": "presentation",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": "spreadsheet",
    "application/vnd.ms-excel": "spreadsheet",
}
_EXTENSION_FAMILY = {
    ".pdf": "pdf", ".docx": "office", ".docm": "office", ".dotx": "office", ".doc": "office", ".rtf": "office",
    ".odt": "office",
    ".pptx": "presentation", ".pptm": "presentation", ".ppsx": "presentation", ".ppt": "presentation",
    ".odp": "presentation",
    ".xlsx": "spreadsheet", ".xlsm": "spreadsheet", ".xls": "spreadsheet", ".csv": "spreadsheet",
    ".tsv": "spreadsheet", ".ods": "spreadsheet",
    ".html": "html", ".htm": "html",
    ".png": "image", ".jpg": "image", ".jpeg": "image", ".tiff": "image", ".tif": "image",
    ".gif": "image", ".bmp": "image", ".webp": "image",
    ".mp3": "audio_video", ".wav": "audio_video", ".m4a": "audio_video", ".aac": "audio_video",
    ".ogg": "audio_video", ".oga": "audio_video", ".opus": "audio_video", ".flac": "audio_video",
    ".mp4": "audio_video", ".m4v": "audio_video", ".mov": "audio_video", ".webm": "audio_video",
    ".mkv": "audio_video", ".avi": "audio_video",
}
_NOT_INDEXABLE = {".zip", ".gz", ".tar", ".7z", ".rar", ".exe", ".dll", ".so", ".dylib", ".bin",
                  ".jar", ".class", ".pyc", ".o", ".a", ".woff", ".woff2", ".ttf", ".eot", ".ico"}


def sniff(raw: bytes) -> str | None:
    """Content sniffing first; libmagic when present, pure-Python otherwise."""
    try:
        import magic  # type: ignore[import-not-found]
        return magic.from_buffer(raw[:8192], mime=True)
    except Exception:  # noqa: BLE001 — libmagic missing on this host
        pass
    try:
        import puremagic
        matches = puremagic.magic_string(raw[:8192])
        return matches[0].mime_type or None if matches else None
    except Exception:  # noqa: BLE001
        return None


_OOXML_PARTS = (
    ("word/", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "office"),
    ("ppt/", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "presentation"),
    ("xl/", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "spreadsheet"),
)
_ODF_MIME = {
    "application/vnd.oasis.opendocument.text": "office",
    "application/vnd.oasis.opendocument.presentation": "presentation",
    "application/vnd.oasis.opendocument.spreadsheet": "spreadsheet",
}


def office_package(raw: bytes) -> tuple[str, str] | None:
    """
    (mime, family) for an OOXML or OpenDocument zip, read from the package itself.
    Magic numbers cannot tell these apart — they are all zips — so sniffers often
    call every one of them a Word document.
    """
    if not raw.startswith(b"PK"):
        return None
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as package:
            names = package.namelist()
            if "mimetype" in names:
                mime = package.read("mimetype")[:100].decode("ascii", "ignore").strip()
                if mime in _ODF_MIME:
                    return mime, _ODF_MIME[mime]
    except (zipfile.BadZipFile, OSError, KeyError):
        return None
    if "[Content_Types].xml" in names:
        for prefix, mime, family in _OOXML_PARTS:
            if any(name.startswith(prefix) for name in names):
                return mime, family
    return None


def looks_textual(raw: bytes) -> bool:
    sample = raw[:8192]
    if b"\x00" in sample:
        return False
    try:
        sample.decode("utf-8")
        return True
    except UnicodeDecodeError as error:
        # A multi-byte character cut by the 8 KB window is still text.
        return error.start >= len(sample) - 4


def classify(path: str, raw: bytes) -> tuple[str, str]:
    """Returns (mime, family)."""
    pure = PurePosixPath(path)
    suffix = pure.suffix.lower()
    package = office_package(raw)
    if package:
        return package
    mime = sniff(raw) or ""
    if raw.startswith(b"PK") and (mime in _BINARY_MIME or mime in _ODF_MIME):
        # The sniffer guessed an Office type from the zip signature alone, but the
        # package has no Office parts: it is just an archive.
        mime = "application/zip"

    if mime in _BINARY_MIME:
        return mime, _BINARY_MIME[mime]
    # Icons and fonts sniff as images, but there is nothing in them to read.
    if suffix in _NOT_INDEXABLE:
        raise PermanentError("not_indexable", f"{suffix} files carry no text")
    if mime.startswith(("image/", "audio/", "video/")):
        return mime, "image" if mime.startswith("image/") else "audio_video"
    if suffix in _EXTENSION_FAMILY and not looks_textual(raw):
        return mime or "application/octet-stream", _EXTENSION_FAMILY[suffix]
    if suffix in _NOT_INDEXABLE or not looks_textual(raw):
        raise PermanentError("not_indexable", f"binary content ({mime or 'unknown type'})")

    # Text: the extension decides which kind of text.
    if suffix in {".html", ".htm"}:
        return "text/html", "html"
    if suffix in {".csv", ".tsv"}:
        return "text/csv", "spreadsheet"
    if suffix in MARKDOWN:
        return ("application/x-ipynb+json" if suffix == ".ipynb" else "text/markdown"), "markdown"
    if pure.name.lower() in {"readme", "changelog", "license"}:
        return "text/markdown", "markdown"
    if suffix in CODE_EXTENSIONS:
        return mime or "text/plain", "code"
    return mime or "text/plain", "text"


class DetectStage:
    name, version = "detect", "1"

    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        path = ctx.hint("relative_path") or ctx.filename
        name = PurePosixPath(path).name

        if name in LOCK_FILES:
            raise PermanentError("not_indexable", f"{name} is a dependency lock file")
        if VENDORED.search(path):
            raise PermanentError("not_indexable", "vendored or generated directory")
        if MINIFIED.search(name):
            raise PermanentError("not_indexable", "minified bundle")

        suffix = PurePosixPath(name).suffix.lower()
        if suffix in LEGACY_OFFICE:
            raise PermanentError("unsupported_format",
                                 f"{suffix} is a legacy format; save it as {LEGACY_OFFICE[suffix]} and upload again")

        mime, family = classify(path, ctx.raw or b"")

        if family == "code" and ctx.raw:
            longest = max((len(line) for line in ctx.raw[:200_000].split(b"\n")), default=0)
            if longest > self.settings.max_line_length_for_code:
                raise PermanentError("not_indexable",
                                     f"a {longest}-character line suggests generated or minified code")

        if family in LATER_PHASES:
            raise PermanentError("unsupported_in_this_build",
                                 f"{family} extraction arrives in {LATER_PHASES[family]}")

        ctx.mime, ctx.family = mime, family
        return ctx
