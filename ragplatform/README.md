# Dochub RAG ingestion platform

Consumes Dochub's process queue, turns each uploaded document into searchable chunks
(pgvector + Postgres full-text), and reports every document — and then the whole
artifact batch — back to the Dochub API.

Built from `.design-extract/rag-ingestion-platform-prompt.md`. **Phases 1–5 are
done**: the skeleton, contracts, queue, storage and runner; text, Markdown, notebook
and source-code extraction, a real embedder, atomic indexing, hybrid search and the
Dochub callbacks; PDF, Word, PowerPoint, HTML, images (with OCR) and spreadsheets;
audio and video, transcribed with timestamps; and the LLM stages — document summaries
and contextual chunk headers with prompt caching — plus a grounded `/chat` endpoint
that powers Dochub's Chat tab (see "Phase status").

---

## Architecture

```
 Dochub API                                       ragplatform
 ──────────                                       ───────────
 Process pressed
   │ queue 1  dochub-source-upload-requested
   ▼
 Extractor ── bytes ──► Azure Blob (dochub-documents)
   │  content MD5 from Azure → document_versions
   │ queue 2  dochub-document-process-requested
   ▼
 ┌───────────────────────────────────────────────────────────────────────────┐
 │ rag-ingest worker                                                         │
 │   one queue message = one source batch                                    │
 │   adapter: fan out to one IngestMessage per document                      │
 │                                                                           │
 │   fetch → detect → extract → normalize → enrich → chunk                   │
 │         → contextualize → embed → index          (per document, bounded)  │
 │                                                                           │
 │   per document:  POST /api/ingestion/documents/{id}/processed | failed    │
 │   whole batch:   POST /api/ingestion/artifacts/{id}/processed             │
 └───────────────────────────────────────────────────────────────────────────┘
        │                         │
        ▼                         ▼
 rag-postgres (pgvector)     Azure Blob rag-objects
 documents · chunks ·        canonical DocumentModel JSON
 pipeline_runs               (re-chunk without re-extracting)
        ▲
        │
 rag-ingest api  /search (hybrid RRF) · /health · /metrics · /admin/runs/{id}
```

| Piece | Where |
|---|---|
| Spec contracts (`IngestMessage`, `DocumentModel`, `Chunk`) | `src/rag_ingest/models.py` |
| Dochub's message → per-document messages | `src/rag_ingest/dochub/adapter.py` |
| Callbacks to the API (retrying, `X-Dochub-Service-Key`) | `src/rag_ingest/dochub/callbacks.py` |
| Queue consumers (Postgres mirror of the API's, and Service Bus) | `src/rag_ingest/queue/` |
| Stages | `src/rag_ingest/pipeline/stages/` |
| Extractors / chunkers / embedders | `extractors/`, `chunkers/`, `llm/` |
| PDF / Office / HTML / images | `extractors/docling_ext.py` (Docling: layout, tables, OCR) |
| Spreadsheets | `extractors/spreadsheet.py` (openpyxl, csv) |
| Audio / video | `extractors/media.py` (ffmpeg + faster-whisper), `chunkers/transcript.py` |
| Schema | `migrations/0001_init.sql` (templated for the embedding size) |

### What each format becomes

| Format | Extractor | Chunker | `chunk_type` | Cited by |
|---|---|---|---|---|
| Text | built-in | recursive (paragraph → sentence → word), 60-token overlap | `prose` | line range |
| Markdown, README, `.ipynb` | markdown-it-py / nbformat | headings H1–H3, code fences intact | `prose`, `code`, `table` | section, lines |
| Code | tree-sitter | one chunk per function/method; class signature as header | `code_symbol` | file, lines, symbol |
| PDF, DOCX, ODT, HTML, images | Docling (layout, TableFormer, RapidOCR) | hierarchical: grouped under headings; tables alone, header repeated when split | `prose`, `table` | section, page (–page_end) |
| PPTX, ODP | Docling | one chunk per slide incl. speaker notes; dividers merged | `slide` | slide (–slide_end) |
| XLSX, CSV, TSV | openpyxl / csv | per sheet: a summary chunk, then row groups with the header repeated | `sheet_rows` | sheet, row range |
| MP3, WAV, M4A, AAC, OGG, FLAC, MP4, MOV, WEBM, MKV, AVI | ffmpeg → faster-whisper (word timestamps, VAD) | 60–120 s windows, closed at a pause or speaker turn, max 600 tokens, 1-segment overlap | `transcript` | `start_ms`–`end_ms` |

Docling's "furniture" layer (running headers and footers, page numbers, site
navigation) is dropped; its "notes" layer (speaker notes) is kept. Scans and images go
through OCR, and the document's mean OCR confidence is recorded in
`extraction.ocr_confidence`; below `MIN_OCR_CONFIDENCE` the document **fails** as
`low_quality_extraction` rather than indexing unreadable text. Legacy binary formats
(`.doc`, `.ppt`, `.xls`, `.rtf`, `.ods`) fail with advice to re-save in the modern
format — Docling reads them only through LibreOffice, which this service does not ship.

Recordings: ffmpeg extracts mono 16 kHz audio (from a temp file — MP4 and MOV keep
their index at the end, which a pipe cannot reach), Whisper transcribes it with voice
activity detection, and every Whisper segment becomes a `transcript_segment` element
with its word timings. The spoken language comes from Whisper, not from text
detection. The document records duration, container and codecs, and for video its
dimensions. A recording with no audio track fails as `no_audio_track`; a transcript
whose mean segment probability is below `MIN_TRANSCRIPT_CONFIDENCE` fails as
`low_quality_extraction`. The 33-second test call transcribes in about 5 s on a laptop
CPU with the `base` model; the default `small` model is slower and more accurate.

The first PDF or image after a start takes ~25 s while the layout and OCR models load
(and, on a fresh machine, download into `HF_HOME`); after that each one-page fixture
took about a second on a laptop CPU. Office, HTML and spreadsheet files need no models.
ODT and ODP are routed to Docling too, but have no test fixture yet.

### The LLM stages (phase 5)

With `LLM_PROVIDER` set (`azure_openai`, `openai` or `anthropic`):

- **Summary** (stage 5): one call per document, 3–5 sentences, stored in
  `documents.summary` and in the document-level `search_tsv`. Code files under
  `SUMMARY_MIN_CODE_LINES` (50) get none, per the spec.
- **Contextual retrieval** (stage 7): every non-code chunk gets one or two sentences,
  written from the whole document (and its summary), situating the chunk. They go into
  `contextualized_text` as `Context: …`, which is what is embedded and full-text
  indexed; the raw `text` shown to users is unchanged. Code keeps its deterministic
  header (file, symbol, imports), which the spec specifies for it.
- **Prompt caching**: the document is sent as an identical prefix on every chunk call —
  marked with `cache_control` for Anthropic, placed first with a `prompt_cache_key` for
  OpenAI/Azure. The first call writes the cache, the rest run concurrently
  (`LLM_CONCURRENCY`) and read it; if one fails the rest are cancelled.
- **Untrusted content**: documents and retrieved passages are wrapped in
  `<document>` / `<sources>` delimiters and every prompt says to treat them as data.
- **Retries**: our own, as for embeddings — back off on 429/5xx/overload/timeouts
  honouring Retry-After; 401/403/404 are transient (fix the key, the queue retries);
  other 4xx fail the document.
- **Cost**: `rag_llm_calls_total`, `rag_llm_tokens_total` (input, cached input,
  output) and `rag_llm_cost_usd_total` per model and purpose, priced by `LLM_PRICE_*`.
  Each document also records its own spend in `metadata.llm_usage`.
- **Versioning**: the provider, model and prompt version are part of the effective
  pipeline version, so turning the LLM on, or changing model, re-indexes documents
  the next time they arrive.

Without an LLM nothing breaks: headers stay deterministic and chat returns the
retrieved passages with a note saying no model is configured.

### Reranking (second stage)

First-stage search fuses full-text and vector rankings (RRF). With a reranker set,
the top `RERANK_CANDIDATES` (40) fused candidates are then re-scored by a hosted
cross-encoder:

| `RERANK_PROVIDER` | `RERANK_MODEL` | Key | Notes |
|---|---|---|---|
| `openrouter` (current) | `nvidia/llama-nemotron-rerank-vl-1b-v2:free` | `OPENROUTER_API_KEY` | Free; ~20 requests a minute. |
| `cohere` | `rerank-v3.5` | `COHERE_API_KEY` | Trial keys: 10 a minute. |

Both speak the same request and response, so switching is two settings. The reranker
is a cross-encoder that reads the question and each
passage together, with the passage's document and section prepended — and `/search`
and chat use that order. Each hit carries `rerank_score` (0–1); `RERANK_MIN_SCORE`
drops anything scored below it (0 = keep all) to narrow chat's sources further.

A reranker failure never fails a search: one quick retry on 429/5xx, then the fused
order is returned and `rag_rerank_calls_total{outcome="failed"}` counts it. A Cohere
**trial key allows 10 rerank calls a minute**, so under real load expect fallbacks
until it is a production key.

### Evaluation (phase 7)

```bash
rag-ingest eval                         # configured embedder and reranker, all fixtures
rag-ingest eval --quick --embedding local --rerank off    # free, offline, ~30 s
rag-ingest eval --llm on                # include LLM-written chunk context (costs LLM calls)
rag-ingest eval --rerank-per-minute 9   # pace a Cohere trial key
rag-ingest eval --out report.json       # everything, per question and per mode
```

It builds a throwaway index from `tests/fixtures` in a database of its own (dropped
afterwards; `--keep` keeps it), then asks every question in `tests/eval/golden.jsonl`
— exact identifiers and paraphrases, with look-alike distractors across documents — in
four modes and prints recall@5, recall@20 and MRR, overall and by kind, plus the
questions not fully found. A result is relevant when it comes from the expected file
and contains the expected text, so the set survives chunking changes. Add a question by
adding a line.

First results (Azure `text-embedding-3-small`, no LLM context, 24 questions):

| mode | recall@5 | recall@20 | MRR |
|---|---|---|---|
| lexical | 0.167 | 0.167 | 0.167 |
| semantic | 1.000 | 1.000 | 0.847 |
| hybrid | 1.000 | 1.000 | 0.826 |
| **hybrid + Cohere rerank** | **1.000** | **1.000** | **0.958** |

Reading it: every expected passage is found in the top 5 by vector or fused search;
the reranker's job is order, and it moves the right passage to first place for every
identifier lookup (MRR 0.83 → 1.00) and almost every paraphrase (0.82 → 0.95). The two
not first are answered by the twin fixture (the same statement as PNG and PDF, the same
call as MP3 and MP4). Full-text alone is weak on questions because it requires every
word (`LEXICAL_MATCH=all`); matching any word (`any`) lifts its recall to 0.96 but adds
noise that lowers the fused MRR (0.736–0.811 depending on `LEXICAL_WEIGHT`), and with
the reranker both settings reach 0.958 — so `all` stays the default.

### Chat

`POST /chat` takes the tenant, principals, the conversation (last
`CHAT_HISTORY_TURNS` exchanges) and optional filters (`artifact_id`, or
`artifact_ids` for a team/group scope), and streams server-sent events:
`sources` (the numbered passages, with document, section and page/slide/time/rows),
then `delta`s of answer text, then `done` with the source numbers actually cited and
the token usage. A follow-up question is first rewritten into a standalone search
query. The model is told to answer only from the sources and cite them as `[n]`.

**Every endpoint except `/health` and `/metrics` requires `X-Dochub-Service-Key`**:
search and chat take the tenant and principals from the request, so only Dochub's API,
which derives them from the signed-in user, may call them.

### What "processed" means

- Each document finishes in one of two ways and the API is told either way:
  **processed** (with chunk count, SHA-256, model, pipeline version) or **failed**
  (stage, error, permanent or not). A transient failure is only reported on the last
  attempt — before that the message goes back on the queue with backoff.
- When every document in the batch has been reported, the worker calls the
  **artifact** endpoint. The API — not this service — decides the artifact's status
  from all of its documents: `Processed`, `PartiallyProcessed` or `Failed`.
- A `409 stale_version` reply means a newer upload of that file already superseded
  this one; the worker treats it as done.

### Skipping unchanged files

Dochub stores the MD5 Azure returns for every blob (`document_versions.content_md5`)
and passes it in the message. The fetch stage checks the downloaded bytes against it,
and skips the document entirely when the same content was already indexed with the
same **effective pipeline version** and embedding model. The effective version is
`PIPELINE_VERSION` plus a fingerprint of the document's family's chunker (name and
version) and the embedding size — so bumping a chunker's `version` re-indexes exactly
what it produced, and adding a chunker for a new format re-indexes nothing else.

---

## Running it

Needs the root `docker-compose.yml` (Postgres, Azurite and `rag-postgres` on 5433) and
the Dochub API running with a matching `Ingestion:ServiceKey`.

```bash
cd ragplatform
python3 -m venv .venv && . .venv/bin/activate
pip install -e '.[local,docs,media,dev]'   # embeddings, Docling, Whisper; add ,magic for libmagic
brew install ffmpeg                         # or apt-get install ffmpeg — needed for audio/video
cp .env.example .env                 # set DOCHUB_SERVICE_KEY and an embedding provider
rag-ingest migrate                   # idempotent; the worker also runs it on start
rag-ingest worker                    # consumes the process queue
rag-ingest api                       # http://localhost:8090
```

Or in Docker, alongside everything else:

```bash
docker compose --profile rag up -d --build
```

`DOCHUB_SERVICE_KEY` must equal the API's `Ingestion:ServiceKey` (32+ characters). In
development both already hold the same generated key.

### Configuration

All settings are environment variables (or `.env`); `src/rag_ingest/config.py` is the
full list with defaults.

| Variable | Purpose |
|---|---|
| `DOCHUB_API_URL`, `DOCHUB_SERVICE_KEY` | Where callbacks go and the shared key that authorises them. |
| `QUEUE_BACKEND` | `postgres` (reads `dochub.queue_messages`, the API's `ServiceBus:Enabled=false` mode) or `servicebus`. |
| `PROCESS_QUEUE`, `DOCHUB_QUEUE_DSN`, `SERVICEBUS_CONNECTION_STRING` | The queue to consume. |
| `MAX_ATTEMPTS` | Deliveries before a message is dead-lettered. Default 5, same as the API. |
| `RAG_DATABASE_DSN` | The pgvector database. |
| `BLOB_CONNECTION_STRING`, `OBJECT_CONTAINER` | Where Dochub's blobs are read from and DocumentModels are written to. |
| `EMBEDDING_PROVIDER` | `azure_openai`, `openai` or `local`. There is no fake provider: the service refuses to start without a real one. |
| `EMBEDDING_MODEL`, `EMBEDDING_DIM` | Must agree with the model, and with the database column (checked at start). |
| `DOCLING_ENABLED` | PDF/Office/HTML/image extraction. The worker refuses to start if it is on and Docling is not installed. |
| `OCR_ENGINE`, `OCR_LANGUAGES` | `rapidocr` (default, bundled), `easyocr`, `tesseract` (needs the binary), `ocrmac` (macOS), or `auto`. |
| `MIN_OCR_CONFIDENCE` | Quality gate for scans. Default 0.5. |
| `DOCLING_CONCURRENCY`, `MAX_PAGES`, `DOCLING_TIMEOUT_SECONDS` | Bounds on Docling work. |
| `MEDIA_ENABLED` | Audio/video transcription. The worker refuses to start if it is on and ffmpeg or faster-whisper is missing. |
| `WHISPER_MODEL`, `WHISPER_DEVICE`, `WHISPER_COMPUTE_TYPE`, `WHISPER_LANGUAGE` | `small` / `auto` / `int8` / detect by default. A GPU wants `float16`. |
| `MIN_TRANSCRIPT_CONFIDENCE`, `MAX_MEDIA_SECONDS` | Quality gate (0.35) and length cap (4 h). |
| `TRANSCRIPT_MIN_WINDOW_SECONDS`, `TRANSCRIPT_MAX_WINDOW_SECONDS`, `TRANSCRIPT_PAUSE_MS`, `TRANSCRIPT_MAX_TOKENS` | 60 / 120 / 1500 / 600, per the spec. |
| `SHEET_ROWS_PER_ELEMENT`, `MAX_SHEET_ROWS` | Spreadsheet row grouping and the per-sheet cap (a warning is recorded when it bites). |
| `SLIDE_MAX_TOKENS`, `SLIDE_MIN_TOKENS`, `SHEET_MAX_TOKENS` | 600 / 20 / 600 by default, per the spec. |
| `LLM_PROVIDER`, `LLM_MODEL`, `CHAT_MODEL` | `none` (default), `azure_openai`, `openai` or `anthropic`. Azure takes the chat deployment name as `LLM_MODEL`; Anthropic defaults to `claude-haiku-4-5`. The service refuses to start if the provider's key is missing. |
| `ANTHROPIC_API_KEY` | For `anthropic`. Azure and OpenAI reuse the embedding credentials. |
| `LLM_REASONING_EFFORT`, `CHAT_REASONING_EFFORT`, `LLM_REASONING_BUDGET` | For reasoning models (GPT-5, o-series) on Azure/OpenAI, whose hidden reasoning tokens count against the output limit. `none`/`low` for enrichment keeps the many short calls fast; the budget adds output room. A call that returns no text because the budget ran out fails as `llm_config` with this advice rather than indexing nothing. |
| `LLM_PRICE_INPUT_PER_MTOK`, `LLM_PRICE_CACHED_INPUT_PER_MTOK`, `LLM_PRICE_OUTPUT_PER_MTOK` | Prices for the cost metric. |
| `SUMMARY_ENABLED`, `CONTEXT_ENABLED`, `CONTEXT_MAX_CHUNKS`, `LLM_MAX_DOCUMENT_TOKENS`, `LLM_CONCURRENCY` | Turn each stage off, cap cost per document, cap the document text sent (24k tokens). |
| `RERANK_PROVIDER`, `RERANK_MODEL` | `none`, `openrouter` or `cohere`, and the model for it. |
| `OPENROUTER_API_KEY`, `OPENROUTER_BASE_URL` | For `openrouter` (`https://openrouter.ai/api/v1`). |
| `COHERE_API_KEY`, `COHERE_BASE_URL` | For `cohere` (`https://api.cohere.com`; a dedicated Cohere endpoint goes in the base URL). |
| `RERANK_CANDIDATES`, `RERANK_MIN_SCORE`, `RERANK_TIMEOUT_SECONDS` | Pool re-scored (40), cut-off (0), timeout (10 s). |
| `LEXICAL_MATCH`, `LEXICAL_WEIGHT` | First-stage full-text: `all` words (default) or `any`; its weight in the fusion (1.0). Measured with `rag-ingest eval`. |
| `CHAT_TOP_K`, `CHAT_MAX_TOKENS`, `CHAT_HISTORY_TURNS` | Passages per answer (8), answer length, history kept. |
| `WORKER_CONCURRENCY`, `CHUNK_TARGET_TOKENS`, `CHUNK_MAX_TOKENS` | Tuning. |

### Operating it

- **Health / metrics:** `GET /health` reports pipeline version, embedding model and LLM.
  Prometheus metrics come from **two** processes: the worker on
  `http://localhost:9464/metrics` (`WORKER_METRICS_PORT`) — `rag_stage_duration_seconds`,
  `rag_stage_outcomes_total`, `rag_documents_total` (incl. `deleted`, `acl_updated`),
  `rag_chunks_written_total`, `rag_tokens_embedded_total`, `rag_llm_calls_total`,
  `rag_llm_tokens_total`, `rag_llm_cost_usd_total`, `rag_batches_total`,
  `rag_documents_in_flight`, `rag_batches_in_flight`, and every 30 s
  `rag_queue_messages{state}` (pending, in_flight, dead_lettered…) and
  `rag_queue_lag_seconds` (how long the oldest waiting message has waited); the API on
  `http://localhost:8090/metrics` for search and chat (its LLM calls).
- **Tracing:** set `OTEL_EXPORTER_OTLP_ENDPOINT` (e.g. `http://localhost:4318`, or an
  Azure Monitor / Jaeger / Tempo collector) and both processes export OpenTelemetry
  traces over OTLP/HTTP: one trace per batch with a span per document and per stage
  (tenant, Dochub document id, filename, attempt, chunks, skip reason; failed stages
  marked as errors), and a span per API request. Every log line then carries `trace_id`
  and `span_id`. Unset, tracing costs nothing.
- **Admin** (all need `X-Dochub-Service-Key`):

  | Call | Does |
  |---|---|
  | `POST /admin/reindex` `{"tenant_id": "…", "document_ids": [...]}` | Rebuilds documents through the queue — some, a tenant's (no ids), or everyone's (`{"all": true}`, must be explicit). Reuses the stored DocumentModel, so OCR and transcription don't run again; Dochub isn't called back. Use after changing a chunker, prompt, LLM or embedding model. |
  | `POST /admin/dlq/replay?limit=100` | Puts dead-lettered process messages back with fresh attempts (Postgres and Service Bus). |
  | `GET /admin/queue` | Queue depth by state, and lag. |
  | `GET /admin/runs/{document_id}` | A document and every run: attempt, failed stage, error. |
  | `POST /admin/purge` | Used by Dochub's **Remove**: an upload's documents leave the index. |
- **Concurrency:** a worker holds up to `WORKER_BATCH_CONCURRENCY` uploads (2) and
  `WORKER_CONCURRENCY` documents (4) at once, at most `TENANT_CONCURRENCY` (2) of them for
  one organization, so a large backfill can't starve everyone else. At its batch limit
  it stops pulling from the queue (back-pressure) instead of leasing work it can't
  start. LLM and embedding calls have their own global semaphores. Stopping drains held
  batches first. Scale out by running more workers; the queue gives each message to one.
- **Deletes and permission changes:** a sync that finds a file gone from its source
  (same location only, and never on an empty listing) removes it from Dochub and sends a
  `delete`, which drops its document, chunks and stored model from the index.
  `acl_update` rewrites a document's permissions on it and its chunks without
  re-embedding (spec 2.7). Dochub's permissions are per organization today, so it does
  not send these yet; the path is live and tested.
- **Why did a document fail?** `GET /admin/runs/{document_id}` returns the document
  row and every run with its attempt, failed stage and error. The same error is on the
  document in the Dochub UI.
- **Search** (with the service key):
  ```bash
  curl -s localhost:8090/search -H 'Content-Type: application/json' \
    -H "X-Dochub-Service-Key: $DOCHUB_SERVICE_KEY" -d '{
    "tenant_id": "<organization id>", "principals": ["org:<organization id>"],
    "query": "how are claims acknowledged", "top_k": 5,
    "filters": {"artifact_id": "<artifact id>"}}'
  ```
  Tenant and ACL filters run inside the SQL, never after retrieval.
- **Logs** are structured (`LOG_JSON=true` for JSON) and carry `document_id`,
  `message_id`, `stage` and `attempt`.

### Tests

```bash
pytest                         # 111 tests: 89 unit + 22 integration
pytest -m "not integration"     # no services needed
pytest -m "not slow"            # skip everything that loads Docling or Whisper models
```

Integration tests create throwaway databases on the compose Postgres instances and a
container on Azurite, so `docker compose up -d` must be running. They use the local
embedder; Dochub is replaced by an in-process fake that records every callback.

Binary fixtures (DOCX, PPTX, XLSX, CSV, HTML, a text PDF, a scanned PDF, a PNG, and a
33-second two-voice claims call as MP3 and MP4) are generated by
`tests/fixtures/make_fixtures.py` — readable, and rebuildable (the recordings need macOS
`say` and ffmpeg) — and committed so tests never depend on it. Integration tests use
Whisper `base` to stay quick. The LLM stages and chat are tested with scripted clients
(`tests/fakes.py: RecordingLLM`, and recorded SDK calls for the provider adapters), as
the spec asks; no test calls a real LLM.

---

## Extending it

### Adding a source fetcher

Today every document arrives through Dochub, which has already copied the bytes into
blob storage — so there is one fetcher, `FetchStage`, reading by `raw_object_key`. A new
source that does not go through Dochub needs:

1. A producer that writes the bytes to blob storage and publishes an `IngestMessage`
   (`models.py`) — `source`, `source_item_id`, `source_version` and `acl` are mandatory.
2. Nothing else in the pipeline. If the bytes must be pulled at ingest time instead,
   add a branch to `FetchStage.run` keyed on `message.source` that returns the same
   `ctx.raw` / `ctx.content_hash`; every later stage only sees the context.

### Adding a chunker

1. Implement the `Chunker` protocol in `chunkers/base.py`: a `name`, a `version`, and
   `chunk(model, tenant_id) -> list[Chunk]`. Take a `Budget` in the constructor and use
   `make_chunk` so ids, ordinals and provenance stay consistent.
2. Register it for a content family in `ChunkerRegistry.__init__`, with a budget from
   `budget_for` (which reserves room for the contextual header).
3. Bump its `version` whenever its output changes. That alone changes the effective
   pipeline version, so already-indexed documents are re-chunked the next time they
   arrive.

A new **format** is the same shape one stage earlier: an extractor in `extractors/`
producing a `DocumentModel`, a branch in `ExtractStage`, and removing the family from
`LATER_PHASES` in `detect.py`.

### Changing the embedding model and re-indexing

- **Same dimension** (e.g. a newer model of the same size): change `EMBEDDING_MODEL`.
  Documents indexed with the old model are no longer "already indexed", so each
  re-processes the next time Dochub sends it — a recurring sync, or **Process** again.
- **Different dimension:** the `chunks.embedding` column's size is fixed when the
  schema is created, and the worker refuses to start on a mismatch rather than failing
  on the first insert. Point `RAG_DATABASE_DSN` at a new database, set
  `EMBEDDING_MODEL` / `EMBEDDING_DIM`, run `rag-ingest migrate`, and re-process the
  artifacts. Switch search over once it has caught up, then drop the old database.

---

## Phase status

| Phase | Scope | State |
|---|---|---|
| 1 | Skeleton, contracts, queue, storage, runner, observability | **Done** |
| 2 | Text, Markdown, notebooks, code (tree-sitter: Python, C#, TypeScript/TSX, JavaScript, Java, Go); real embedder; atomic index; hybrid search; Dochub callbacks | **Done** |
| 3 | PDF, Word, PowerPoint, HTML, images (Docling, OCR, tables); XLSX/CSV; hierarchical, table, slide and sheet chunkers; OCR quality gate | **Done** |
| 4 | Audio / video: ffmpeg + faster-whisper with word timestamps, transcript chunker with time windows, transcript quality gate | **Done** |
| 5 | LLM summaries and contextual chunk headers with prompt caching; Azure OpenAI, OpenAI and Anthropic clients with retries and cost metrics; grounded streaming `/chat` | **Done** — tested with mocked clients, and run end to end against Azure OpenAI `gpt-5.5` + `text-embedding-3-small` |
| 6 | Deletes (sync detects removed files), ACL-only updates, admin reindex / DLQ replay / queue state, stored-model reuse, parallel batches with global + per-tenant caps and back-pressure, OpenTelemetry traces with log correlation, queue lag + dead-letter metrics, worker metrics endpoint | **Done** |
| 7 | Retrieval evaluation harness: golden set, `rag-ingest eval` with recall@5/@20 and MRR for lexical, semantic, hybrid and reranked; plus (requested) Cohere Rerank 3.5 as a second stage | **Done** |

## Deviations from the spec

| Spec | Built | Why |
|---|---|---|
| Redis Streams queue (SQS stub) | Postgres queue mirroring the API's, and Azure Service Bus | Dochub already publishes to these; a second broker would only copy messages across. |
| MinIO / S3 object store | Azure Blob (Azurite locally) | Same reason — Dochub's bytes are already there. |
| Raw bytes copied into the platform's store | `raw_object_key` points at Dochub's blob | Dochub's blob paths are immutable per upload (timestamped prefix), so a copy adds cost and nothing else. |
| Platform decides batch outcome | Worker reports per document; the **API** computes the artifact status from all its documents | The API already owns artifact state and sees documents from earlier batches. |
| — | Extra endpoint `POST /api/ingestion/documents/{id}/failed` | A batch can only be closed if failures are reported too, not only successes. |
| python-magic | `puremagic` by default, libmagic with the `magic` extra | Pure-Python install works everywhere; the Docker image includes libmagic. |
| testcontainers | Throwaway databases on the compose instances | Faster, and no Docker-in-test requirement; each test still gets a fresh database. |
| `text-embedding-3-large`, 3072 dims | 1536 by default for Azure OpenAI, 384 for the local model | Half the storage and index size for a small recall cost; 1536 is a supported shortened size of the same model. Above 2000 dims the schema switches to `halfvec` automatically, since pgvector's HNSW index caps `vector` at 2000. |
| Python 3.12 | 3.12 in Docker; tested locally on 3.14 too | — |
| Docling for all Office formats; `unstructured` fallback | Docling only; no `unstructured` fallback | Docling handled every fixture, and a second extractor doubles what has to be tested. A file Docling cannot read fails permanently with Docling's reason. |
| Legacy `.doc`/`.ppt`/`.xls` via Docling | Refused as `unsupported_format` with re-save advice | Docling reads them only through LibreOffice, which this image does not include. |
| XLSX/CSV via openpyxl / pandas | openpyxl + the stdlib `csv` module | Streams rows (read-only mode) without loading a DataFrame; no pandas dependency of our own. |
| OCR engine unspecified | RapidOCR by default (configurable) | Ships its models in the wheel, so it works offline and on CPU. |
| `Provenance` merged span "first page..last page" | `provenance.page` is the first page; `metadata.page_end` / `slide_end` the last | The contract's `Provenance` holds a single page/slide; changing it would change section 4. |
| Merge undersized siblings under the same parent | Also: an undersized *subsection* joins its parent's chunk (up to the target size) | Otherwise short subsections (a 3-item list under its own heading) become 15-token chunks that embed poorly. A full-sized subsection keeps its own heading path. |
| `pyannote` diarization (optional) | Not built; `speaker` is empty | Optional in the spec, and it needs a Hugging Face token and gated model terms. The transcript chunker already breaks on and labels speaker turns when a speaker is present. |
| ffmpeg extracts audio | ffmpeg CLI decodes to 16 kHz float samples handed to Whisper | faster-whisper 1.2.1's own decoder calls a PyAV option that PyAV 19 removed; the CLI is also what the spec names. |
| Video keyframe/slide text | Left as a TODO in `extractors/media.py` | The spec marks it "later". |
| Quality gate for OCR | Also for transcripts (`MIN_TRANSCRIPT_CONFIDENCE`) | The same reasoning applies: a garbled transcript would be retrieved and cited. |
| `LLMClient` for Azure OpenAI | Azure OpenAI, OpenAI (also any OpenAI-compatible endpoint via `OPENAI_BASE_URL`) and Anthropic | Same protocol; lets you use whichever you have keys for. |
| Local `bge-m3` embedder | Local embedder is `BAAI/bge-small-en-v1.5` via fastembed (any fastembed model works) | Small and CPU-friendly for development; switch with `EMBEDDING_MODEL`/`EMBEDDING_DIM`. |
| `/search` "to verify the index — not the product" | Also `/chat`, behind the service key | Requested for Dochub's Chat tab; outside the spec's scope. |
| Contextual header for every chunk | LLM context for non-code chunks; code keeps the deterministic header | The spec's code rule (path + imports + class signature) already situates code. |
| Long documents | The LLM sees the first `LLM_MAX_DOCUMENT_TOKENS` (24k); documents with more than `CONTEXT_MAX_CHUNKS` chunks keep deterministic headers | Cost guard; both are recorded as warnings on the run. |
| Eval golden set of query → expected chunk/document **ids** | Query → expected **file + text** | Chunk ids change with every chunker change; file + text is what "found the answer" means and survives them. |
| Reranker "TODO(phase 7)" | Cohere Rerank 3.5 over the public API | Requested; the dedicated endpoint given refused TLS from this machine, so `COHERE_BASE_URL` is the public API until it accepts connections. |
| Redis Streams consumer groups for N workers | Several batches per worker process, plus as many worker processes as you like on one queue | The queue is Dochub's (Postgres or Service Bus), which already gives each message to one consumer. |
| OpenTelemetry auto-instrumentation | Manual spans (batch, document, stage, API request) over OTLP/HTTP | The spans that matter here, without a per-library instrumentation package for each dependency. |
| Admin reindex enqueues IngestMessages | Enqueues Dochub-shaped process messages marked `reindex` | One consumer and one message shape; the worker fans them out exactly as it does uploads. |
| `deleted_at` soft delete | A delete removes the document row (chunks cascade) | Dochub keeps the history (`document_versions`); the index only needs what is current. |
| PII detection with Presidio (stage 4) | Not built | Not listed in any phase's deliverables; say if you want it added. |
| Language detection | `lingua` in high-accuracy mode over a fixed set of ~28 languages | Low-accuracy mode labelled plain English Markdown as Welsh, which silently disabled English stemming for it. Fixed in this phase; affects phase 2 Markdown too. |
