"""
Embedders (spec stage 8). Every implementation here calls a real model.

  azure_openai  Azure OpenAI deployment of text-embedding-3-*
  openai        OpenAI or any OpenAI-compatible endpoint
  local         an ONNX model run on this machine through fastembed
"""

from __future__ import annotations

import asyncio
import math
import random
import threading
from typing import Protocol

import structlog

from rag_ingest.config import Settings
from rag_ingest.llm.tokenizer import HuggingFaceTokenizer, TiktokenTokenizer, Tokenizer
from rag_ingest.pipeline.errors import PermanentError, TransientError

log = structlog.get_logger(__name__)


class Embedder(Protocol):
    model: str
    dim: int
    max_input_tokens: int
    tokenizer: Tokenizer

    async def embed(self, texts: list[str]) -> list[list[float]]: ...
    async def embed_query(self, text: str) -> list[float]: ...


def validate(vectors: list[list[float]], expected: int, count: int) -> None:
    if len(vectors) != count:
        raise TransientError("embedding_count_mismatch", f"asked for {count}, got {len(vectors)}")
    for index, vector in enumerate(vectors):
        if len(vector) != expected:
            raise PermanentError("embedding_dimension_mismatch",
                                 f"vector {index} has {len(vector)} dimensions, expected {expected}")
        if any(math.isnan(x) or math.isinf(x) for x in vector):
            raise TransientError("embedding_not_finite", f"vector {index} contains NaN or infinity")
        if not any(vector):
            raise TransientError("embedding_all_zero", f"vector {index} is all zeros")


class _OpenAIBase:
    """Shared retry policy: back off on 429/5xx/timeouts only, honouring Retry-After."""

    def __init__(self, settings: Settings) -> None:
        self.model = settings.embedding_model
        self.dim = settings.embedding_dim
        self.max_input_tokens = 8191
        self.tokenizer: Tokenizer = TiktokenTokenizer("cl100k_base")
        self._max_retries = settings.embedding_max_retries
        self._client = self._build_client(settings)
        self._model_param = self.model

    def _build_client(self, settings: Settings):  # pragma: no cover - overridden
        raise NotImplementedError

    async def embed(self, texts: list[str]) -> list[list[float]]:
        import openai

        for attempt in range(1, self._max_retries + 1):
            try:
                response = await self._client.embeddings.create(
                    model=self._model_param, input=texts, dimensions=self.dim,
                )
                vectors = [item.embedding for item in sorted(response.data, key=lambda d: d.index)]
                validate(vectors, self.dim, len(texts))
                return vectors
            except (openai.RateLimitError, openai.APITimeoutError, openai.APIConnectionError,
                    openai.InternalServerError) as error:
                if attempt == self._max_retries:
                    raise TransientError("embedding_unavailable", str(error)) from error
                await asyncio.sleep(self._delay(error, attempt))
            except openai.APIStatusError as error:
                if error.status_code >= 500:
                    if attempt == self._max_retries:
                        raise TransientError("embedding_unavailable", str(error)) from error
                    await asyncio.sleep(self._delay(error, attempt))
                    continue
                if error.status_code in (401, 403, 404):
                    # Configuration, not content: retrying the batch later is right
                    # once someone fixes the key or deployment name.
                    raise TransientError("embedding_config", f"{error.status_code}: {error.message}") from error
                raise PermanentError("embedding_rejected", f"{error.status_code}: {error.message}") from error
        raise TransientError("embedding_unavailable", "retries exhausted")

    async def embed_query(self, text: str) -> list[float]:
        return (await self.embed([text]))[0]

    @staticmethod
    def _delay(error: Exception, attempt: int) -> float:
        response = getattr(error, "response", None)
        header = response.headers.get("retry-after") if response is not None else None
        if header:
            try:
                return min(60.0, float(header))
            except ValueError:
                pass
        return min(60.0, 2 ** attempt) * (0.5 + random.random())


class AzureOpenAIEmbedder(_OpenAIBase):
    def _build_client(self, settings: Settings):
        import openai
        return openai.AsyncAzureOpenAI(
            azure_endpoint=settings.azure_openai_endpoint,
            api_key=settings.azure_openai_api_key.get_secret_value(),
            api_version=settings.azure_openai_api_version,
            timeout=settings.embedding_timeout_seconds,
            max_retries=0,  # retries are ours, so the policy above is the only one
        )

    def __init__(self, settings: Settings) -> None:
        super().__init__(settings)
        # Azure addresses the model by deployment name.
        self._model_param = settings.azure_openai_embedding_deployment


class OpenAIEmbedder(_OpenAIBase):
    def _build_client(self, settings: Settings):
        import openai
        return openai.AsyncOpenAI(
            api_key=settings.openai_api_key.get_secret_value(),
            base_url=settings.openai_base_url,
            timeout=settings.embedding_timeout_seconds,
            max_retries=0,
        )


class LocalEmbedder:
    """
    Runs an ONNX embedding model locally through fastembed. Real vectors, no
    network once the model is cached — useful where data must not leave the host.
    """

    # fastembed model → (HF tokenizer repo, max input tokens, query instruction handled by fastembed)
    _KNOWN = {
        "BAAI/bge-small-en-v1.5": ("BAAI/bge-small-en-v1.5", 512),
        "BAAI/bge-base-en-v1.5": ("BAAI/bge-base-en-v1.5", 512),
        "BAAI/bge-large-en-v1.5": ("BAAI/bge-large-en-v1.5", 512),
        "sentence-transformers/all-MiniLM-L6-v2": ("sentence-transformers/all-MiniLM-L6-v2", 256),
        "nomic-ai/nomic-embed-text-v1.5": ("nomic-ai/nomic-embed-text-v1.5", 8192),
    }

    def __init__(self, settings: Settings) -> None:
        try:
            from fastembed import TextEmbedding
        except ImportError as error:
            raise ValueError("EMBEDDING_PROVIDER=local needs the 'local' extra: pip install -e '.[local]'") from error

        self.model = settings.embedding_model
        if self.model not in self._KNOWN:
            raise ValueError(
                f"Unknown local model '{self.model}'. Supported: {', '.join(sorted(self._KNOWN))}."
            )
        repo, self.max_input_tokens = self._KNOWN[self.model]
        self._model = TextEmbedding(model_name=self.model)
        self.tokenizer = HuggingFaceTokenizer(repo)

        probe = next(iter(self._model.embed(["dimension probe"])))
        self.dim = len(probe)
        if self.dim != settings.embedding_dim:
            raise ValueError(
                f"{self.model} produces {self.dim}-dimension vectors but EMBEDDING_DIM={settings.embedding_dim}."
            )
        # A thread lock, taken inside the worker thread: one ONNX session is used
        # serially rather than oversubscribing cores, and unlike an asyncio.Lock it
        # is not tied to whichever event loop touched it first.
        self._lock = threading.Lock()

    def _run(self, fn) -> list[list[float]]:
        with self._lock:
            return [v.tolist() for v in fn()]

    async def embed(self, texts: list[str]) -> list[list[float]]:
        vectors = await asyncio.to_thread(self._run, lambda: self._model.embed(texts))
        validate(vectors, self.dim, len(texts))
        return vectors

    async def embed_query(self, text: str) -> list[float]:
        # bge models expect an instruction prefix on queries; fastembed adds it.
        vectors = await asyncio.to_thread(self._run, lambda: self._model.query_embed([text]))
        validate(vectors, self.dim, 1)
        return vectors[0]


def build_embedder(settings: Settings) -> Embedder:
    if settings.embedding_provider == "azure_openai":
        return AzureOpenAIEmbedder(settings)
    if settings.embedding_provider == "openai":
        return OpenAIEmbedder(settings)
    return LocalEmbedder(settings)
