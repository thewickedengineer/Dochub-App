"""
Audio and video (spec stage 3): ffmpeg extracts the audio, faster-whisper
transcribes it with word timestamps, and every Whisper segment becomes a
`transcript_segment` element carrying `start_ms`/`end_ms`.

ffmpeg reads from a temporary file rather than a pipe: MP4 and MOV keep their
index at the end of the file, and a pipe cannot seek back to it.

Speaker diarization (pyannote) is optional in the spec and not built here, so
`speaker` is left empty; the transcript chunker already splits on speaker turns
when a speaker is present. TODO(spec, later): extract slide/keyframe text from
video frames.

Blocking and CPU-heavy: callers run `extract` in a thread.
"""

from __future__ import annotations

import json
import math
import shutil
import subprocess
import tempfile
import threading
from pathlib import Path, PurePosixPath
from typing import Any

import numpy as np

from rag_ingest.config import Settings
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.pipeline.errors import PermanentError, TransientError

NAME, VERSION = "faster-whisper", "1"
SAMPLE_RATE = 16_000


def available() -> str | None:
    """None when ffmpeg, ffprobe and faster-whisper are all present, otherwise what is missing."""
    missing = [tool for tool in ("ffmpeg", "ffprobe") if shutil.which(tool) is None]
    try:
        import faster_whisper  # noqa: F401
    except ImportError:
        missing.append("faster-whisper (pip install -e '.[media]')")
    return ", ".join(missing) or None


# One model per configuration, shared across the process: loading it is slow.
_MODELS: dict[tuple, Any] = {}
_MODELS_LOCK = threading.Lock()


def _model(settings: Settings):
    key = (settings.whisper_model, settings.whisper_device, settings.whisper_compute_type)
    with _MODELS_LOCK:
        if key not in _MODELS:
            from faster_whisper import WhisperModel
            _MODELS[key] = WhisperModel(settings.whisper_model, device=settings.whisper_device,
                                        compute_type=settings.whisper_compute_type)
        return _MODELS[key]


def probe(path: Path) -> dict[str, Any]:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", str(path)],
        capture_output=True, text=True, timeout=120)
    if result.returncode != 0:
        raise PermanentError("extraction_failed", f"ffprobe could not read the file: {result.stderr.strip()[:300]}")
    return json.loads(result.stdout or "{}")


def decode(path: Path, timeout: float) -> np.ndarray:
    """Mono 16 kHz float32 samples — what Whisper expects."""
    try:
        result = subprocess.run(
            ["ffmpeg", "-nostdin", "-loglevel", "error", "-i", str(path), "-vn", "-ac", "1",
             "-ar", str(SAMPLE_RATE), "-f", "f32le", "pipe:1"],
            capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise TransientError("extraction_timeout", "ffmpeg took too long to decode the audio") from error
    if result.returncode != 0:
        raise PermanentError("extraction_failed", f"ffmpeg could not decode the audio: "
                                                  f"{result.stderr.decode(errors='replace').strip()[:300]}")
    return np.frombuffer(result.stdout, dtype=np.float32)


def _media_metadata(info: dict[str, Any]) -> tuple[dict[str, Any], bool]:
    """Duration, container and codecs, and whether there is any audio at all."""
    streams = info.get("streams") or []
    # A cover image in an MP3 is a "video" stream with a single frame; it is not a video.
    video = next((s for s in streams if s.get("codec_type") == "video"
                  and not (s.get("disposition") or {}).get("attached_pic")), None)
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    duration = float((info.get("format") or {}).get("duration") or (audio or {}).get("duration") or 0)
    out: dict[str, Any] = {
        "duration_ms": int(round(duration * 1000)),
        "has_video": video is not None,
        "container": (info.get("format") or {}).get("format_name"),
        "audio_codec": (audio or {}).get("codec_name"),
    }
    if video is not None:
        out["video"] = {"codec": video.get("codec_name"), "width": video.get("width"),
                        "height": video.get("height")}
    tags = (info.get("format") or {}).get("tags") or {}
    for key in ("title", "artist", "date", "creation_time"):
        if tags.get(key):
            out[key] = tags[key]
    return out, audio is not None


def extract(document_id: str, content_hash: str, content_type: str, raw: bytes, filename: str,
            settings: Settings) -> DocumentModel:
    suffix = PurePosixPath(filename).suffix.lower() or ".bin"
    with tempfile.TemporaryDirectory(prefix="rag-media-") as tmp:
        path = Path(tmp) / f"input{suffix}"
        path.write_bytes(raw)
        metadata, has_audio = _media_metadata(probe(path))
        if not has_audio:
            # TODO(spec): keyframe/slide text for silent video.
            raise PermanentError("no_audio_track", "the file has no audio to transcribe")
        if metadata["duration_ms"] > settings.max_media_seconds * 1000:
            raise PermanentError("too_long", f"{metadata['duration_ms'] // 1000} s exceeds "
                                             f"MAX_MEDIA_SECONDS={settings.max_media_seconds}")
        # Generous: decoding runs far faster than real time.
        audio = decode(path, timeout=max(120.0, metadata["duration_ms"] / 1000))

    if audio.size < SAMPLE_RATE // 2:
        raise PermanentError("low_quality_extraction", "less than half a second of audio decoded")

    segments, info = _model(settings).transcribe(
        audio, language=settings.whisper_language or None, beam_size=settings.whisper_beam_size,
        word_timestamps=True, vad_filter=True)

    elements: list[Element] = []
    weighted, total = 0.0, 0.0
    for segment in segments:
        text = segment.text.strip()
        if not text:
            continue
        start_ms, end_ms = int(segment.start * 1000), int(segment.end * 1000)
        span = max(segment.end - segment.start, 0.01)
        weighted += math.exp(segment.avg_logprob) * span
        total += span
        elements.append(Element(
            id=element_id(document_id, len(elements)),
            type="transcript_segment", text=text,
            provenance=Provenance(start_ms=start_ms, end_ms=end_ms),
            attributes={
                "avg_logprob": round(segment.avg_logprob, 3),
                "no_speech_prob": round(segment.no_speech_prob, 3),
                "words": [[w.word.strip(), int(w.start * 1000), int(w.end * 1000)] for w in (segment.words or [])],
            },
        ))

    confidence = round(weighted / total, 4) if total else None
    detected = info.language if info.language_probability >= 0.5 else None
    return DocumentModel(
        document_id=document_id, content_hash=content_hash, content_type=content_type,
        content_family="audio_video", title=metadata.pop("title", None),
        language=detected,
        metadata={**metadata, "speech_ms": int(total * 1000)},
        elements=elements,
        extraction={
            "extractor": NAME, "version": VERSION, "model": settings.whisper_model,
            "language": info.language, "language_probability": round(info.language_probability, 3),
            "transcript_confidence": confidence, "diarization": False, "warnings": [],
        },
    )
