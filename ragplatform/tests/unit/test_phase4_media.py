"""Phase 4: transcript windows (fast) and ffmpeg + faster-whisper end to end (slow)."""

import shutil
import subprocess

import pytest

from rag_ingest.chunkers.base import Budget
from rag_ingest.chunkers.transcript import TranscriptChunker, Windows
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance
from rag_ingest.pipeline.errors import PermanentError
from support.offline import FIXTURES, TOKENIZER as TOK, offline_settings, run_offline

WINDOWS = Windows(min_ms=60_000, max_ms=120_000, pause_ms=1_500)


def _transcript(spans: list[tuple[int, int, str, str | None]]) -> DocumentModel:
    elements = [Element(id=element_id("doc_t", i), type="transcript_segment", text=text,
                        provenance=Provenance(start_ms=start, end_ms=end, speaker=speaker))
                for i, (start, end, text, speaker) in enumerate(spans)]
    return DocumentModel(document_id="doc_t", content_hash="h", content_type="audio/mpeg",
                         content_family="audio_video", elements=elements)


def _chunks(model, budget=Budget(600, 600, 20, 0)):
    return TranscriptChunker(TOK, budget, WINDOWS).chunk(model, "t")


def test_a_short_recording_is_one_window_with_its_time_span():
    [chunk] = _chunks(_transcript([(0, 4_000, "Hello.", None), (5_000, 9_000, "Hi there.", None)]))
    assert chunk.chunk_type == "transcript" and chunk.text == "Hello.\nHi there."
    assert (chunk.provenance.start_ms, chunk.provenance.end_ms) == (0, 9_000)


def test_windows_close_at_a_pause_once_long_enough_and_overlap_by_one_segment():
    # 10-second segments, contiguous, except a 3 s pause after the 7th (ends at 70 s).
    spans, t = [], 0
    for i in range(12):
        spans.append((t, t + 10_000, f"Segment {i} about the claim.", None))
        t += 10_000 + (3_000 if i == 6 else 0)
    first, second = _chunks(_transcript(spans))
    assert first.provenance.end_ms == 70_000                     # closed at the pause, after 60 s
    assert second.text.startswith("Segment 6 about the claim.")  # one segment of overlap
    assert second.provenance.start_ms == 60_000 and second.provenance.end_ms == spans[-1][1]


def test_windows_never_exceed_the_maximum_length_without_a_natural_break():
    spans = [(i * 10_000, (i + 1) * 10_000, f"Uninterrupted sentence number {i}.", None) for i in range(30)]
    chunks = _chunks(_transcript(spans))
    assert len(chunks) >= 3
    assert all(c.provenance.end_ms - c.provenance.start_ms <= 120_000 for c in chunks)
    # Consecutive windows share exactly one segment.
    assert all(b.provenance.start_ms == a.provenance.end_ms - 10_000 for a, b in zip(chunks, chunks[1:]))


def test_a_speaker_turn_is_a_break_and_speakers_are_labelled():
    spans = [(i * 10_000, (i + 1) * 10_000, f"Handler line {i}.", "Handler") for i in range(7)]
    spans += [(70_000, 80_000, "Caller replies.", "Caller")]
    first, second = _chunks(_transcript(spans))
    assert first.provenance.speaker == "Handler" and first.text.startswith("Handler: Handler line 0.")
    assert "Caller: Caller replies." in second.text and second.metadata["speakers"] == ["Caller", "Handler"]


def test_the_token_budget_closes_a_window_before_the_time_limit():
    spans = [(i * 1_000, (i + 1) * 1_000, "word " * 40, None) for i in range(20)]
    chunks = _chunks(_transcript(spans), Budget(100, 100, 10, 0))
    assert len(chunks) > 5 and all(c.token_count <= 100 for c in chunks)


# ── ffmpeg + Whisper (slow) ───────────────────────────────────────────────────

needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
BASE = offline_settings(whisper_model="base")


@pytest.mark.slow
@needs_ffmpeg
async def test_an_audio_call_is_transcribed_with_timestamps_and_confidence():
    ctx = await run_offline("fnol-call.mp3", settings=BASE)
    model = ctx.model
    assert ctx.family == "audio_video" and model.language == "en"
    assert 32_000 <= model.metadata["duration_ms"] <= 34_000 and not model.metadata["has_video"]
    assert model.extraction["transcript_confidence"] > 0.6
    segments = model.elements
    assert all(e.type == "transcript_segment" for e in segments)
    assert all(e.provenance.start_ms < e.provenance.end_ms for e in segments)
    assert [e.provenance.start_ms for e in segments] == sorted(e.provenance.start_ms for e in segments)
    assert all(e.attributes["words"] for e in segments)           # word-level timestamps kept
    text = " ".join(e.text for e in segments)
    assert "reversed into my parked car" in text and "4472" in text

    [chunk] = ctx.chunks
    assert chunk.chunk_type == "transcript" and "Time: 00:00–00:3" in chunk.contextualized_text
    bakery = next(e for e in segments if "bakery" in e.text)
    assert 15_000 < bakery.provenance.start_ms < 22_000           # deep link lands where it was said


@pytest.mark.slow
@needs_ffmpeg
async def test_a_video_records_its_dimensions_and_is_transcribed():
    ctx = await run_offline("fnol-call.mp4", settings=BASE)
    meta = ctx.model.metadata
    assert meta["has_video"] and meta["video"] == {"codec": "h264", "width": 320, "height": 240}
    assert "exchanged details outside the bakery" in " ".join(c.text for c in ctx.chunks)


@pytest.mark.slow
@needs_ffmpeg
async def test_a_silent_video_fails_with_a_reason(tmp_path):
    path = tmp_path / "silent.mp4"
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=5",
                    "-t", "2", "-pix_fmt", "yuv420p", str(path)], check=True)
    with pytest.raises(PermanentError) as error:
        await run_offline("silent.mp4", raw=path.read_bytes(), settings=BASE)
    assert error.value.reason == "no_audio_track"


@pytest.mark.slow
@needs_ffmpeg
async def test_an_unreadable_recording_fails_permanently():
    with pytest.raises(PermanentError) as error:
        await run_offline("broken.mp3", raw=b"ID3" + b"\x00" * 64, settings=BASE)
    assert error.value.reason in {"extraction_failed", "low_quality_extraction"}


@pytest.mark.slow
@needs_ffmpeg
async def test_a_transcript_below_the_confidence_floor_fails_as_low_quality():
    with pytest.raises(PermanentError) as error:
        await run_offline("fnol-call.mp3", settings=offline_settings(whisper_model="base",
                                                                      min_transcript_confidence=0.99))
    assert error.value.reason == "low_quality_extraction" and "transcript confidence" in str(error.value)
