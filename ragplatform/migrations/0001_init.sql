-- Placeholders filled from config by the migration runner:
--   {vector_type}  vector or halfvec   (halfvec above 2000 dimensions)
--   {embedding_dim}
--   {vector_ops}   vector_cosine_ops or halfvec_cosine_ops

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE documents (
  document_id          text PRIMARY KEY,
  tenant_id            text NOT NULL,
  source               text NOT NULL,
  source_item_id       text NOT NULL,
  source_uri           text NOT NULL,
  source_version       text,
  content_hash         text NOT NULL,
  content_type         text NOT NULL,
  content_family       text NOT NULL,
  title                text,
  language             text,
  summary              text,
  metadata             jsonb NOT NULL DEFAULT '{}',
  acl_allow            text[] NOT NULL DEFAULT '{}',
  acl_deny             text[] NOT NULL DEFAULT '{}',
  raw_object_key       text NOT NULL,
  model_object_key     text,
  pipeline_version     text NOT NULL,
  embedding_model      text,
  status               text NOT NULL,
  error                text,
  -- Dochub's identifiers, so a chunk can be traced to the record a person sees.
  artifact_id          text,
  source_document_id   text,
  document_version_id  text,
  search_tsv           tsvector,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  deleted_at           timestamptz,
  UNIQUE (tenant_id, source, source_item_id)
);

CREATE INDEX documents_search_tsv ON documents USING gin (search_tsv);
CREATE INDEX documents_artifact   ON documents (artifact_id);

CREATE TABLE chunks (
  chunk_id             text PRIMARY KEY,
  document_id          text NOT NULL REFERENCES documents ON DELETE CASCADE,
  tenant_id            text NOT NULL,
  artifact_id          text,
  ordinal              int  NOT NULL,
  text                 text NOT NULL,
  contextualized_text  text NOT NULL,
  token_count          int  NOT NULL,
  chunk_type           text NOT NULL,
  heading_path         text[] NOT NULL DEFAULT '{}',
  provenance           jsonb NOT NULL,
  metadata             jsonb NOT NULL DEFAULT '{}',
  acl_allow            text[] NOT NULL DEFAULT '{}',
  acl_deny             text[] NOT NULL DEFAULT '{}',
  content_family       text NOT NULL,
  chunker_name         text NOT NULL,
  chunker_version      text NOT NULL,
  pipeline_version     text NOT NULL,
  embedding_model      text NOT NULL,
  embedding_dim        int  NOT NULL,
  embedding            {vector_type}({embedding_dim}) NOT NULL,
  -- Per-row text-search configuration: 'english' stems prose, 'simple' leaves
  -- code identifiers intact. to_tsvector(regconfig, text) is immutable, so it
  -- can drive a generated column.
  ts_config            regconfig NOT NULL DEFAULT 'english',
  tsv                  tsvector GENERATED ALWAYS AS (to_tsvector(ts_config, contextualized_text)) STORED,
  created_at           timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX chunks_tsv_gin        ON chunks USING gin (tsv);
CREATE INDEX chunks_embedding_hnsw ON chunks USING hnsw (embedding {vector_ops});
CREATE INDEX chunks_tenant         ON chunks (tenant_id, content_family);
CREATE INDEX chunks_artifact       ON chunks (artifact_id);
CREATE INDEX chunks_acl_gin        ON chunks USING gin (acl_allow);
CREATE INDEX chunks_document       ON chunks (document_id, ordinal);

CREATE TABLE pipeline_runs (
  run_id           uuid PRIMARY KEY,
  document_id      text NOT NULL,
  message_id       text NOT NULL,
  content_hash     text NOT NULL,
  pipeline_version text NOT NULL,
  status           text NOT NULL,          -- running | succeeded | failed | skipped
  attempt          int  NOT NULL DEFAULT 1,
  failed_stage     text,
  permanent        boolean,
  stages           jsonb NOT NULL DEFAULT '[]',
  started_at       timestamptz NOT NULL DEFAULT now(),
  finished_at      timestamptz,
  error            text
);
CREATE UNIQUE INDEX processed_messages ON pipeline_runs (message_id);
CREATE INDEX pipeline_runs_document    ON pipeline_runs (document_id, started_at DESC);
