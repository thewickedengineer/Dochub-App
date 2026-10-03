"""Failure classes. The distinction decides retry versus dead-letter."""

from __future__ import annotations


class StageError(Exception):
    permanent: bool = False

    def __init__(self, reason: str, detail: str = "") -> None:
        self.reason = reason
        self.detail = detail
        super().__init__(f"{reason}: {detail}" if detail else reason)


class PermanentError(StageError):
    """Retrying cannot help: unsupported type, corrupt file, empty extraction."""
    permanent = True


class TransientError(StageError):
    """Worth retrying: a timeout, a 429, a dependency briefly down."""
    permanent = False
