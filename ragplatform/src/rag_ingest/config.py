"""All configuration, from the environment. Every limit and model name lives here."""

from __future__ import annotations

from functools import lru_cache
from typing import Literal

from pydantic import Field, SecretStr, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    # ── Pipeline identity ──────────────────────────────────────────────────────
    # Bumping this re-indexes everything: a document is skipped only when its
    # content hash, pipeline version AND embedding model all match.
    pipeline_version: str = "2.0.0"

    # ── Dochub ─────────────────────────────────────────────────────────────────
    dochub_api_url: str = "http://localhost:5080"
    dochub_service_key: SecretStr = SecretStr("")
    callback_timeout_seconds: float = 15.0

    # ── Queue (Dochub's process queue) ─────────────────────────────────────────
    queue_backend: Literal["postgres", "servicebus"] = "postgres"
    process_queue: str = "dochub-document-process-requested"
    dochub_queue_dsn: str = "postgresql://dochub:dochub@localhost:5432/dochub"
    servicebus_connection_string: SecretStr = SecretStr("")
    servicebus_namespace: str = ""
    queue_lock_seconds: int = 300
    queue_idle_poll_seconds: float = 2.0
    max_attempts: int = 5

    # ── Storage ────────────────────────────────────────────────────────────────
    blob_connection_string: SecretStr = SecretStr(
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
    )
    # DocumentModel JSON lives here. Raw bytes are not copied: Dochub's blob
    # prefix is per-upload and never overwritten, so it already is immutable.
    object_container: str = "rag-objects"
    rag_database_dsn: str = "postgresql://rag:rag@localhost:5433/rag"
    db_pool_min: int = 1
    db_pool_max: int = 10

    # ── Embeddings ─────────────────────────────────────────────────────────────
    embedding_provider: Literal["azure_openai", "openai", "local"] = "azure_openai"
    embedding_model: str = "text-embedding-3-large"
    # 1536 rather than 3072: text-embedding-3 is Matryoshka-trained, and 3072
    # exceeds pgvector's 2000-dimension limit for an HNSW index on `vector`.
    embedding_dim: int = 1536
    embedding_batch_tokens: int = 8000
    embedding_concurrency: int = 4
    embedding_timeout_seconds: float = 60.0
    embedding_max_retries: int = 6

    azure_openai_endpoint: str = ""
    azure_openai_api_key: SecretStr = SecretStr("")
    azure_openai_api_version: str = "2024-10-21"
    azure_openai_embedding_deployment: str = ""

    openai_api_key: SecretStr = SecretStr("")
    openai_base_url: str | None = None

    # ── LLM (summaries, contextual headers, chat answers) ──────────────────────
    # "none" keeps the deterministic headers and answers chat with sources only.
    llm_provider: Literal["none", "azure_openai", "openai", "anthropic"] = "none"
    # The small, fast model for enrichment (spec 2.4). For Azure, a deployment name.
    llm_model: str = ""
    # The model that writes chat answers; empty means llm_model.
    chat_model: str = ""
    anthropic_api_key: SecretStr = SecretStr("")
    # Reasoning models (OpenAI/Azure o-series, GPT-5): reasoning tokens count against
    # the output limit. Empty leaves the model's default; "none"/"low" keep the short
    # enrichment calls fast and cheap. The budget is added on top of each call's limit.
    llm_reasoning_effort: str = ""
    chat_reasoning_effort: str = ""
    llm_reasoning_budget: int = 0
    llm_timeout_seconds: float = 60.0
    llm_max_retries: int = 6
    llm_concurrency: int = Field(default=4, ge=1)
    # Price per million tokens, for the cost metric. Zero means "not configured".
    llm_price_input_per_mtok: float = 0.0
    llm_price_cached_input_per_mtok: float = 0.0
    llm_price_output_per_mtok: float = 0.0
    # The document text handed to the LLM is cut to this many tokens.
    llm_max_document_tokens: int = 24_000
    summary_enabled: bool = True
    summary_max_tokens: int = 300
    # Spec: skip the summary for code files under N lines.
    summary_min_code_lines: int = 50
    context_enabled: bool = True
    context_max_tokens: int = 120
    # Above this many chunks a document keeps deterministic headers (cost guard).
    context_max_chunks: int = 400
    # ── First-stage search ─────────────────────────────────────────────────────
    # "any": a passage matches on any of the question's words; "all": on every one.
    lexical_match: Literal["any", "all"] = "all"
    # The full-text list's weight in Reciprocal Rank Fusion (the vector list's is 1).
    lexical_weight: float = Field(default=1.0, ge=0, le=5)

    # ── Reranking (second-stage scoring of the fused candidates) ───────────────
    rerank_provider: Literal["none", "cohere", "openrouter"] = "none"
    rerank_model: str = "rerank-v3.5"
    cohere_api_key: SecretStr = SecretStr("")
    # The public API; a dedicated (private) Cohere endpoint goes here instead.
    cohere_base_url: str = "https://api.cohere.com"
    # OpenRouter's rerank endpoint takes the same request and answers in the same
    # shape (e.g. model nvidia/llama-nemotron-rerank-vl-1b-v2:free).
    openrouter_api_key: SecretStr = SecretStr("")
    openrouter_base_url: str = "https://openrouter.ai/api/v1"
    # How many fused candidates the reranker re-orders; it returns the top_k of them.
    rerank_candidates: int = Field(default=40, ge=1, le=1000)
    # Drop results the reranker scores below this (0 keeps everything it ranks).
    rerank_min_score: float = Field(default=0.0, ge=0, le=1)
    rerank_timeout_seconds: float = 10.0
    rerank_max_tokens_per_doc: int = 4096

    chat_top_k: int = 8
    chat_max_tokens: int = 900
    chat_history_turns: int = 6

    # ── Limits ─────────────────────────────────────────────────────────────────
    max_file_bytes: int = 50 * 1024 * 1024
    max_line_length_for_code: int = 1000
    # Documents in progress at once, across every batch this worker holds.
    worker_concurrency: int = Field(default=4, ge=1)
    # Batches (Dochub uploads) held at once. The worker stops pulling from the queue
    # while it holds this many — back-pressure, rather than leases it can't serve.
    worker_batch_concurrency: int = Field(default=2, ge=1)
    # Documents in progress at once for one organization, so one tenant's backfill
    # cannot take every slot. 0 means no per-tenant cap.
    tenant_concurrency: int = Field(default=2, ge=0)

    # ── Office / PDF / images (Docling) ────────────────────────────────────────
    # The worker refuses to start without Docling rather than failing every PDF.
    docling_enabled: bool = True
    ocr_engine: Literal["rapidocr", "easyocr", "tesseract", "ocrmac", "auto"] = "rapidocr"
    # OCR languages in the engine's own codes; empty means the engine's default.
    ocr_languages: list[str] = []
    # Below this mean OCR confidence a document fails as low_quality_extraction.
    min_ocr_confidence: float = Field(default=0.5, ge=0, le=1)
    max_pages: int = 2000
    docling_timeout_seconds: float = 900.0
    # Conversions run in threads; layout and OCR models are memory-hungry.
    docling_concurrency: int = Field(default=1, ge=1)

    # ── Audio / video (ffmpeg + faster-whisper) ────────────────────────────────
    # The worker refuses to start without ffmpeg and faster-whisper while this is on.
    media_enabled: bool = True
    whisper_model: str = "small"
    whisper_device: Literal["auto", "cpu", "cuda"] = "auto"
    whisper_compute_type: str = "int8"
    # ISO 639-1 code to force a language; empty lets Whisper detect it.
    whisper_language: str = ""
    whisper_beam_size: int = 5
    whisper_concurrency: int = Field(default=1, ge=1)
    max_media_seconds: int = 4 * 3600
    # Mean per-segment probability below this fails the file as low_quality_extraction.
    min_transcript_confidence: float = Field(default=0.35, ge=0, le=1)

    # ── Spreadsheets ───────────────────────────────────────────────────────────
    sheet_rows_per_element: int = 50
    max_sheet_rows: int = 20_000

    # ── Chunking (tokens, in the embedding model's own tokenizer) ──────────────
    chunk_target_tokens: int = 450
    chunk_max_tokens: int = 800
    chunk_min_tokens: int = 80
    chunk_overlap_tokens: int = 60
    markdown_split_levels: int = 3
    document_split_levels: int = 3
    slide_max_tokens: int = 600
    # A slide below this (a section divider, "Questions?") joins its neighbour.
    slide_min_tokens: int = 20
    sheet_max_tokens: int = 600
    # Transcript windows (spec: ~60–120 s, max 600 tokens, ~1 segment overlap).
    transcript_max_tokens: int = 600
    transcript_min_window_seconds: int = 60
    transcript_max_window_seconds: int = 120
    transcript_pause_ms: int = 1500

    # ── API / ops ──────────────────────────────────────────────────────────────
    api_host: str = "0.0.0.0"
    api_port: int = 8090
    # The worker's own Prometheus endpoint: its stage, document, LLM and queue numbers
    # live in its process, not the API's. 0 turns it off.
    worker_metrics_port: int = 9464
    log_level: str = "INFO"
    log_json: bool = True

    @model_validator(mode="after")
    def _check_embedding_provider(self) -> "Settings":
        # There is no fake runtime embedder: refusing to start beats indexing
        # vectors that mean nothing.
        if self.embedding_provider == "azure_openai":
            missing = [
                name for name, value in [
                    ("AZURE_OPENAI_ENDPOINT", self.azure_openai_endpoint),
                    ("AZURE_OPENAI_API_KEY", self.azure_openai_api_key.get_secret_value()),
                    ("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", self.azure_openai_embedding_deployment),
                ] if not value
            ]
            if missing:
                raise ValueError(
                    "EMBEDDING_PROVIDER=azure_openai needs " + ", ".join(missing)
                    + ". Or set EMBEDDING_PROVIDER=local to embed on this machine."
                )
        if self.embedding_provider == "openai" and not self.openai_api_key.get_secret_value():
            raise ValueError("EMBEDDING_PROVIDER=openai needs OPENAI_API_KEY.")
        if self.llm_provider != "none":
            needs = {
                "azure_openai": [("AZURE_OPENAI_ENDPOINT", self.azure_openai_endpoint),
                                 ("AZURE_OPENAI_API_KEY", self.azure_openai_api_key.get_secret_value()),
                                 ("LLM_MODEL (the chat deployment name)", self.llm_model)],
                "openai": [("OPENAI_API_KEY", self.openai_api_key.get_secret_value()), ("LLM_MODEL", self.llm_model)],
                "anthropic": [("ANTHROPIC_API_KEY", self.anthropic_api_key.get_secret_value())],
            }[self.llm_provider]
            missing = [name for name, value in needs if not value]
            if missing:
                raise ValueError(f"LLM_PROVIDER={self.llm_provider} needs " + ", ".join(missing)
                                 + ". Or set LLM_PROVIDER=none.")
        if self.rerank_provider == "cohere" and not self.cohere_api_key.get_secret_value():
            raise ValueError("RERANK_PROVIDER=cohere needs COHERE_API_KEY. Or set RERANK_PROVIDER=none.")
        if self.rerank_provider == "openrouter" and not self.openrouter_api_key.get_secret_value():
            raise ValueError("RERANK_PROVIDER=openrouter needs OPENROUTER_API_KEY. Or set RERANK_PROVIDER=none.")
        if self.chunk_max_tokens < self.chunk_target_tokens:
            raise ValueError("CHUNK_MAX_TOKENS must be at least CHUNK_TARGET_TOKENS.")
        return self


@lru_cache
def get_settings() -> Settings:
    return Settings()
