"""
Tables (spec stage 6): never split mid-row, and when a table must be split,
repeat its header row in every part so each part still reads as a table.
"""

from __future__ import annotations

from rag_ingest.chunkers.base import split_lines
from rag_ingest.llm.tokenizer import Tokenizer


def _is_separator(line: str) -> bool:
    body = line.replace("|", "").strip()
    return bool(body) and set(body) <= set("-: ")


def header_and_rows(markdown: str) -> tuple[str, list[str]]:
    """A Markdown table's header (with its separator line) and its body rows."""
    lines = [line for line in markdown.split("\n") if line.strip()]
    if len(lines) > 1 and _is_separator(lines[1]):
        return "\n".join(lines[:2]), lines[2:]
    return (lines[0] if lines else ""), lines[1:]


def split_table(tokenizer: Tokenizer, markdown: str, max_tokens: int) -> list[str]:
    """The whole table if it fits; otherwise row groups, each led by the header."""
    if tokenizer.count(markdown) <= max_tokens:
        return [markdown]
    header, rows = header_and_rows(markdown)
    room = max(1, max_tokens - tokenizer.count(header) - 1)
    return [f"{header}\n{part}" for part in split_lines(tokenizer, "\n".join(rows), room)]
