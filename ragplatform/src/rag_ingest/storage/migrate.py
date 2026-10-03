"""Plain-SQL migrations, applied in filename order and recorded once each."""

from __future__ import annotations

from pathlib import Path

import psycopg
import structlog

from rag_ingest.config import Settings

log = structlog.get_logger(__name__)

MIGRATIONS_DIR = Path(__file__).resolve().parents[3] / "migrations"
# pgvector's HNSW index on `vector` stops at 2000 dimensions; halfvec goes to 4000.
VECTOR_HNSW_LIMIT = 2000


def vector_spec(dim: int) -> dict[str, str]:
    halfvec = dim > VECTOR_HNSW_LIMIT
    return {
        "vector_type": "halfvec" if halfvec else "vector",
        "vector_ops": "halfvec_cosine_ops" if halfvec else "vector_cosine_ops",
        "embedding_dim": str(dim),
    }


def migrate(settings: Settings, migrations_dir: Path = MIGRATIONS_DIR) -> list[str]:
    """Apply pending migrations; returns the names applied."""
    applied: list[str] = []
    spec = vector_spec(settings.embedding_dim)

    with psycopg.connect(settings.rag_database_dsn, autocommit=True) as conn:
        conn.execute(
            "CREATE TABLE IF NOT EXISTS schema_migrations ("
            " name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"
        )
        done = {row[0] for row in conn.execute("SELECT name FROM schema_migrations")}

        for path in sorted(migrations_dir.glob("*.sql")):
            if path.name in done:
                continue
            sql = path.read_text()
            for key, value in spec.items():
                sql = sql.replace("{" + key + "}", value)
            with conn.transaction():
                conn.execute(sql)
                conn.execute("INSERT INTO schema_migrations (name) VALUES (%s)", (path.name,))
            applied.append(path.name)
            log.info("migration.applied", name=path.name, **spec)

    verify_dimension(settings)
    return applied


def verify_dimension(settings: Settings) -> None:
    """
    The column's dimension is fixed when the schema is created. Changing the
    embedding model to one with a different size needs a re-index into a new
    column, so refuse to start rather than fail on the first insert.
    """
    with psycopg.connect(settings.rag_database_dsn) as conn:
        row = conn.execute(
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a "
            "WHERE a.attrelid = 'chunks'::regclass AND a.attname = 'embedding'"
        ).fetchone()
    if row is None:
        return
    declared = row[0]  # e.g. "vector(384)"
    expected = f"{vector_spec(settings.embedding_dim)['vector_type']}({settings.embedding_dim})"
    if declared != expected:
        raise RuntimeError(
            f"chunks.embedding is {declared} but EMBEDDING_DIM={settings.embedding_dim} "
            f"expects {expected}. Changing the embedding size means re-indexing — see "
            "'Changing the embedding model' in the README."
        )
