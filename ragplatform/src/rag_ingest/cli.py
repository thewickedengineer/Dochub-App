"""rag-ingest worker | api | migrate"""

from __future__ import annotations

import argparse
import asyncio

from rag_ingest.config import get_settings
from rag_ingest.observability import configure_logging


def main() -> None:
    parser = argparse.ArgumentParser(prog="rag-ingest")
    parser.add_argument("command", choices=["worker", "api", "migrate", "eval"])
    args, rest = parser.parse_known_args()
    if args.command == "eval":
        from rag_ingest.eval import main as evaluate
        raise SystemExit(evaluate(rest))

    settings = get_settings()
    configure_logging(settings.log_level, settings.log_json)
    from rag_ingest.observability import configure_tracing
    configure_tracing(f"rag-ingest-{args.command}")

    if args.command == "migrate":
        from rag_ingest.storage.migrate import migrate
        applied = migrate(settings)
        print(f"applied: {applied or 'nothing to do'}")
    elif args.command == "worker":
        from rag_ingest.worker import run
        asyncio.run(run(settings))
    else:
        import uvicorn

        from rag_ingest.api.app import create_app
        uvicorn.run(create_app(settings), host=settings.api_host, port=settings.api_port, log_config=None)


if __name__ == "__main__":
    main()
