"""Assembles the stage list from its dependencies."""

from __future__ import annotations

import asyncio
import hashlib

from rag_ingest.chunkers.registry import ChunkerRegistry
from rag_ingest.config import Settings
from rag_ingest.llm.client import LLMClient, fingerprint
from rag_ingest.llm.embedder import Embedder
from rag_ingest.llm.prompts import PROMPT_VERSION
from rag_ingest.pipeline.runner import Runner
from rag_ingest.pipeline.stages.chunk import ChunkStage
from rag_ingest.pipeline.stages.contextualize import ContextualizeStage
from rag_ingest.pipeline.stages.detect import DetectStage
from rag_ingest.pipeline.stages.embed import EmbedStage
from rag_ingest.pipeline.stages.enrich import EnrichStage
from rag_ingest.pipeline.stages.extract import ExtractStage
from rag_ingest.pipeline.stages.fetch import FetchStage
from rag_ingest.pipeline.stages.index import IndexStage
from rag_ingest.pipeline.stages.normalize import NormalizeStage
from rag_ingest.storage.blob import BlobStore
from rag_ingest.storage.repository import Repository


class VersionPolicy:
    """
    The version a document of a given content family is indexed under:
    PIPELINE_VERSION plus a short fingerprint of that family's chunker and the
    embedding size. Bumping a chunker re-indexes exactly what it produced
    (spec 2.5) — and adding a chunker for a new family re-indexes nothing else.
    """

    def __init__(self, base: str, registry: ChunkerRegistry, embedding_dim: int, contextualizer: str = "") -> None:
        self.base = base
        self.registry = registry
        self.embedding_dim = embedding_dim
        # Which model writes chunk context: switching it changes what gets embedded.
        self.contextualizer = contextualizer

    def for_family(self, family: str) -> str:
        material = f"{self.registry.fingerprint(family)}|dim={self.embedding_dim}"
        if self.contextualizer:
            material += f"|ctx={self.contextualizer}"
        return f"{self.base}+{hashlib.sha256(material.encode()).hexdigest()[:8]}"


def build_runner(settings: Settings, blobs: BlobStore, repo: Repository, embedder: Embedder,
                 llm: LLMClient | None = None) -> Runner:
    registry = ChunkerRegistry(settings, embedder.tokenizer, embedder.max_input_tokens)
    embed_slots = asyncio.Semaphore(settings.embedding_concurrency)
    llm_slots = asyncio.Semaphore(settings.llm_concurrency)
    contextualizer = "" if llm is None or not settings.context_enabled else f"{fingerprint(llm)}:p{PROMPT_VERSION}"
    versions = VersionPolicy(settings.pipeline_version, registry, embedder.dim, contextualizer)
    return Runner([
        FetchStage(settings, blobs, repo, versions),
        DetectStage(settings),
        ExtractStage(blobs, settings),
        NormalizeStage(settings),
        EnrichStage(settings, llm, embedder.tokenizer),
        ChunkStage(registry),
        ContextualizeStage(embedder.tokenizer, embedder.max_input_tokens, settings, llm, llm_slots),
        EmbedStage(settings, embedder, embed_slots),
        IndexStage(repo, settings.embedding_model, versions),
    ], pipeline_version=settings.pipeline_version, versions=versions)
