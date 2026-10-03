"""
Code (spec stage 6): one chunk per function or method; small classes whole;
large classes one chunk per method with the class signature and docstring as
context. File path and imports are context, not chunk text.
"""

from __future__ import annotations

from rag_ingest.chunkers.base import Budget, make_chunk, split_lines
from rag_ingest.llm.tokenizer import Tokenizer, truncate
from rag_ingest.models import Chunk, DocumentModel

# Imports can run long; this much is enough to say what a file depends on.
IMPORTS_BUDGET = 120


class CodeChunker:
    name = "code_ast"
    version = "1"

    def __init__(self, tokenizer: Tokenizer, budget: Budget) -> None:
        self.tokenizer = tokenizer
        self.budget = budget

    @property
    def key(self) -> str:
        return f"{self.name}:{self.version}"

    def chunk(self, model: DocumentModel, tenant_id: str) -> list[Chunk]:
        tok, b = self.tokenizer, self.budget
        path = model.metadata.get("path") or model.title or ""
        imports = ""
        for element in model.elements:
            if element.attributes.get("symbol_kind") == "file_header":
                imports = truncate(tok, element.text, IMPORTS_BUDGET)
                break

        out: list[tuple[str, list, list[str], dict]] = []
        for element in model.elements:
            attrs = element.attributes
            kind = attrs.get("symbol_kind")
            name = attrs.get("symbol_name")
            if kind == "file_header":
                continue

            base_meta = {"symbol_kind": kind, "symbol_name": name, "language": element.language,
                         "file_path": path, "imports": imports or None}
            heading = [path] + ([name] if name else [])

            if tok.count(element.text) <= b.max:
                out.append((element.text, [element], heading, base_meta))
                continue

            methods = attrs.get("methods") or []
            if methods:
                signature = attrs.get("signature") or ""
                docstring = attrs.get("docstring")
                # The class still needs to be findable as a whole.
                overview = signature + (f'\n    """{docstring}"""' if docstring else "") + "\n" + "\n".join(
                    f"    {m['kind'].replace('_', ' ')}: {m['name']}" for m in methods if m.get("name"))
                out.append((truncate(tok, overview, b.max), [element], heading,
                            {**base_meta, "part": "class_overview"}))
                for method in methods:
                    meta = {**base_meta, "symbol_kind": method["kind"], "symbol_name": method["name"],
                            "class_name": name, "class_signature": signature, "class_docstring": docstring}
                    path_for = heading + ([method["name"]] if method.get("name") else [])
                    parts = split_lines(tok, method["text"], b.max)
                    for index, part in enumerate(parts):
                        out.append((part, [element], path_for,
                                    {**meta, "part": f"{index + 1}/{len(parts)}" if len(parts) > 1 else None,
                                     "line_range": method["line_range"]}))
                continue

            parts = split_lines(tok, element.text, b.max)
            for index, part in enumerate(parts):
                out.append((part, [element], heading, {**base_meta, "part": f"{index + 1}/{len(parts)}"}))

        if not out:
            # An imports-only file is still a file someone might look for.
            header = next((e for e in model.elements if e.attributes.get("symbol_kind") == "file_header"), None)
            if header is not None:
                out.append((header.text, [header], [path], {"symbol_kind": "file_header", "file_path": path,
                                                            "language": header.language}))

        return [
            make_chunk(model, tenant_id, self.key, ordinal, text, elements, heading, "code_symbol", tok, **meta)
            for ordinal, (text, elements, heading, meta) in enumerate(out)
        ]
