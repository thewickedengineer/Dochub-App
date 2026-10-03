"""Test-only doubles. Nothing here is reachable from the runtime code paths."""

from __future__ import annotations

from rag_ingest.llm.tokenizer import TiktokenTokenizer
from rag_ingest.pipeline.errors import TransientError


class FailingEmbedder:
    """Always reports a transient outage — used to drive the retry/DLQ path."""

    def __init__(self, dim: int) -> None:
        self.model = "failing-test-embedder"
        self.dim = dim
        self.max_input_tokens = 512
        self.tokenizer = TiktokenTokenizer()
        self.calls = 0

    async def embed(self, texts: list[str]) -> list[list[float]]:
        self.calls += 1
        raise TransientError("embedding_unavailable", "simulated outage (test double)")

    async def embed_query(self, text: str) -> list[float]:
        raise TransientError("embedding_unavailable", "simulated outage (test double)")


class RecordingLLM:
    """
    A scripted LLMClient: records every call and answers deterministically.
    Spec phase 5: "tests use mocked clients".
    """

    provider = "fake"

    def __init__(self, answer: str = "Claims are acknowledged within 24 hours [1].", fail_on: str | None = None,
                 fail_after: int = 0) -> None:
        from rag_ingest.llm.client import LLMResult, LLMUsage

        self._result, self._usage = LLMResult, LLMUsage
        self.model = "fake-small"
        self.chat_model = "fake-chat"
        self.answer = answer
        self.calls: list[dict] = []
        self.fail_on = fail_on
        self.fail_after = fail_after

    def _maybe_fail(self, purpose: str) -> None:
        if self.fail_on == purpose and sum(c["purpose"] == purpose for c in self.calls) > self.fail_after:
            raise TransientError("llm_unavailable", "simulated outage (test double)")

    async def complete(self, *, purpose, system, prompt, max_tokens, document=None, cache_key=None):
        self.calls.append({"purpose": purpose, "system": system, "prompt": prompt, "document": document,
                           "cache_key": cache_key, "max_tokens": max_tokens})
        self._maybe_fail(purpose)
        if purpose == "summary":
            text = "This guide explains how claims are handled, from intake to settlement."
        elif purpose == "query_rewrite":
            text = "standalone: " + prompt.splitlines()[-2].split(": ", 1)[-1]
        else:
            passage = prompt.split("<passage>\n", 1)[-1].split("\n</passage>", 1)[0]
            text = f"From the claims guide, about {' '.join(passage.split()[:4])}."
        usage = self._usage(input_tokens=100, cached_input_tokens=80 if len(self.calls) > 1 else 0,
                            output_tokens=20, calls=1)
        return self._result(text, self.model, usage)

    async def stream(self, *, purpose, system, messages, max_tokens, context=None):
        self.calls.append({"purpose": purpose, "system": system, "messages": messages, "context": context})
        self._maybe_fail(purpose)
        for word in self.answer.split(" "):
            yield word + " "
        yield self._result(self.answer, self.chat_model, self._usage(input_tokens=500, output_tokens=12, calls=1))
