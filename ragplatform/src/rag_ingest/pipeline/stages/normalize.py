"""Stage 4 — clean text, detect language, refuse to index nothing."""

from __future__ import annotations

import asyncio
import re
import unicodedata
from collections import Counter
from functools import lru_cache

from rag_ingest.config import Settings
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.errors import PermanentError

_HYPHEN_BREAK = re.compile(r"(\w)-\n(\w)")
_SPACES = re.compile(r"[ \t ]+")
_BLANK_RUNS = re.compile(r"\n{3,}")

MIN_TEXT_CHARS = 20

# Postgres full-text configurations, keyed by ISO 639-1. Anything else uses
# 'simple', which does not stem — safe, if less clever.
TS_CONFIGS = {
    "en": "english", "fr": "french", "de": "german", "es": "spanish", "it": "italian",
    "pt": "portuguese", "nl": "dutch", "sv": "swedish", "da": "danish", "nb": "norwegian",
    "fi": "finnish", "ru": "russian", "tr": "turkish", "hu": "hungarian", "ro": "romanian",
}


def clean_prose(text: str) -> str:
    text = unicodedata.normalize("NFKC", text)
    text = _HYPHEN_BREAK.sub(r"\1\2", text)
    text = "\n".join(_SPACES.sub(" ", line).strip() for line in text.split("\n"))
    return _BLANK_RUNS.sub("\n\n", text).strip()


def clean_code(text: str) -> str:
    # NFC, not NFKC: compatibility folding can rewrite identifiers and literals.
    text = unicodedata.normalize("NFC", text)
    return "\n".join(line.rstrip() for line in text.split("\n")).strip("\n")


def strip_repeated_page_lines(elements) -> int:
    """Drop lines that recur on more than half the pages: running headers and footers."""
    pages = {e.provenance.page for e in elements if e.provenance.page is not None}
    if len(pages) < 3:
        return 0
    seen: Counter[str] = Counter()
    for page in pages:
        lines = {line.strip() for e in elements if e.provenance.page == page for line in e.text.split("\n")}
        seen.update(line for line in lines if line)
    repeated = {line for line, count in seen.items() if count > len(pages) / 2}
    removed = 0
    for element in elements:
        kept = [line for line in element.text.split("\n") if line.strip() not in repeated]
        removed += len(element.text.split("\n")) - len(kept)
        element.text = "\n".join(kept)
    return removed


# Languages Postgres can stem, plus common ones it cannot — those still need a
# correct label so they get 'simple' instead of the wrong stemmer.
_DETECTABLE = set(TS_CONFIGS) | {"zh", "ja", "ko", "ar", "hi", "pl", "cs", "el", "he", "uk", "vi", "th", "id"}


@lru_cache(maxsize=1)
def _detector():
    from lingua import IsoCode639_1, LanguageDetectorBuilder
    # High-accuracy mode: low-accuracy mode labels plain English Markdown as Welsh.
    # Restricting the candidate set keeps its memory bounded.
    codes = [getattr(IsoCode639_1, code.upper()) for code in sorted(_DETECTABLE)]
    return LanguageDetectorBuilder.from_iso_codes_639_1(*codes).build()


def detect_language(sample: str) -> str | None:
    if len(sample) < 40:
        return None
    language = _detector().detect_language_of(sample[:5000])
    return language.iso_code_639_1.name.lower() if language else None


class NormalizeStage:
    name, version = "normalize", "2"

    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    async def run(self, ctx: PipelineContext) -> PipelineContext:
        # Cleaning and language detection are CPU work; keep them off the event loop.
        return await asyncio.to_thread(self._normalize, ctx)

    def _normalize(self, ctx: PipelineContext) -> PipelineContext:
        model = ctx.model
        assert model is not None

        # Quality gate for scans and recordings: unreadable OCR or a garbled
        # transcript is worse than nothing, because it would be retrieved and
        # cited. Fail it loudly instead.
        for key, floor, setting in (
                ("ocr_confidence", self.settings.min_ocr_confidence, "MIN_OCR_CONFIDENCE"),
                ("transcript_confidence", self.settings.min_transcript_confidence, "MIN_TRANSCRIPT_CONFIDENCE")):
            confidence = model.extraction.get(key)
            if confidence is not None and confidence < floor:
                raise PermanentError("low_quality_extraction",
                                     f"{key.replace('_', ' ')} {confidence:.2f} is below {setting}={floor}")

        removed = strip_repeated_page_lines(model.elements)
        if removed:
            ctx.warnings.append(f"removed {removed} repeated header/footer lines")

        for element in model.elements:
            element.text = clean_code(element.text) if element.type == "code" else clean_prose(element.text)
        for warning in model.extraction.get("warnings") or []:
            ctx.warnings.append(str(warning))
        model.elements = [e for e in model.elements if e.text.strip()]

        total = sum(len(e.text.strip()) for e in model.elements)
        if total < MIN_TEXT_CHARS:
            # Never index garbage silently.
            raise PermanentError("low_quality_extraction", f"only {total} characters of text extracted")

        # Code and spreadsheet cells are not prose; a language guess on them is noise.
        prose = " ".join(e.text for e in model.elements if e.type != "code")
        if ctx.family == "audio_video":
            pass                      # Whisper detected the spoken language from the audio itself
        else:
            model.language = detect_language(prose) if ctx.family not in ("code", "spreadsheet") else None

        prose_config = TS_CONFIGS.get(model.language or "", "english" if not model.language else "simple")
        ctx.ts_configs = {"*": prose_config, "code_symbol": "simple", "code": "simple"}
        return ctx
