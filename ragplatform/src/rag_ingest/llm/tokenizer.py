"""Token counting in the embedding model's own tokenizer (spec 2.3)."""

from __future__ import annotations

from typing import Protocol


class Tokenizer(Protocol):
    name: str

    def encode(self, text: str) -> list[int]: ...
    def decode(self, ids: list[int]) -> str: ...

    def count(self, text: str) -> int: ...


class TiktokenTokenizer:
    """OpenAI's text-embedding-3 models use cl100k_base."""

    def __init__(self, encoding: str = "cl100k_base") -> None:
        import tiktoken
        self._enc = tiktoken.get_encoding(encoding)
        self.name = f"tiktoken:{encoding}"

    def encode(self, text: str) -> list[int]:
        return self._enc.encode(text, disallowed_special=())

    def decode(self, ids: list[int]) -> str:
        return self._enc.decode(ids)

    def count(self, text: str) -> int:
        return len(self.encode(text))


class HuggingFaceTokenizer:
    """For local models: the exact tokenizer the model was trained with."""

    def __init__(self, repo_id: str) -> None:
        from tokenizers import Tokenizer as HFTokenizer
        self._tok = HFTokenizer.from_pretrained(repo_id)
        self._tok.no_truncation()
        self._tok.no_padding()
        self.name = f"hf:{repo_id}"

    def encode(self, text: str) -> list[int]:
        return self._tok.encode(text, add_special_tokens=False).ids

    def decode(self, ids: list[int]) -> str:
        return self._tok.decode(ids)

    def count(self, text: str) -> int:
        return len(self.encode(text))


def truncate(tokenizer: Tokenizer, text: str, max_tokens: int) -> str:
    ids = tokenizer.encode(text)
    return text if len(ids) <= max_tokens else tokenizer.decode(ids[:max_tokens])
