"""
Runs detect → extract → normalize → enrich → chunk → contextualize on bytes in
memory: no queue, no database, no blob store, no embedder. For unit tests of
what an extractor and its chunker produce together.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from rag_ingest.chunkers.registry import ChunkerRegistry
from rag_ingest.config import Settings
from rag_ingest.ids import document_id
from rag_ingest.llm.tokenizer import TiktokenTokenizer
from rag_ingest.models import IngestMessage
from rag_ingest.pipeline.context import PipelineContext
from rag_ingest.pipeline.stages.chunk import ChunkStage
from rag_ingest.pipeline.stages.contextualize import ContextualizeStage
from rag_ingest.pipeline.stages.detect import DetectStage
from rag_ingest.pipeline.stages.enrich import EnrichStage
from rag_ingest.pipeline.stages.extract import ExtractStage
from rag_ingest.pipeline.stages.normalize import NormalizeStage

FIXTURES = Path(__file__).parents[1] / "fixtures"
TOKENIZER = TiktokenTokenizer()
MODEL_MAX = 8191


class MemoryBlobs:
    def __init__(self) -> None:
        self.written: dict[str, dict] = {}

    async def write_json(self, key: str, value: dict) -> str:
        self.written[key] = value
        return key


def offline_settings(**overrides) -> Settings:
    return Settings(_env_file=None, embedding_provider="local", embedding_dim=384, **overrides)


_extractors: dict[int, ExtractStage] = {}


async def run_offline(name: str, raw: bytes | None = None, settings: Settings | None = None,
                      relative_path: str | None = None) -> PipelineContext:
    settings = settings or offline_settings()
    raw = (FIXTURES / name).read_bytes() if raw is None else raw
    path = relative_path or name
    message = IngestMessage(
        message_id=f"test:{name}", tenant_id="org-test", source="upload", source_item_id=path,
        source_uri=f"memory://{path}", source_version=None, enqueued_at=datetime.now(timezone.utc),
        hints={"filename": Path(path).name, "relative_path": path, "artifact_name": "Fixtures"},
    )
    ctx = PipelineContext(message=message, document_id=document_id("org-test", "upload", path))
    ctx.raw, ctx.content_hash = raw, "sha-" + name

    # One ExtractStage (and so one Docling converter) per settings object: model loading is slow.
    extract = _extractors.get(id(settings))
    if extract is None:
        extract = _extractors[id(settings)] = ExtractStage(MemoryBlobs(), settings)
    registry = ChunkerRegistry(settings, TOKENIZER, MODEL_MAX)
    for stage in (DetectStage(settings), extract, NormalizeStage(settings), EnrichStage(),
                  ChunkStage(registry), ContextualizeStage(TOKENIZER, MODEL_MAX)):
        ctx = await stage.run(ctx)
    return ctx
