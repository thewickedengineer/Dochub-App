"""
Source code through tree-sitter: one element per top-level symbol (function,
class, interface, …) plus a file header holding imports and the module docstring.
Classes carry their methods in attributes so the chunker can split a large one
by method while keeping the class signature as context.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import PurePosixPath

from rag_ingest.extractors.base import decode_text
from rag_ingest.ids import element_id
from rag_ingest.models import DocumentModel, Element, Provenance

NAME, VERSION = "code_treesitter", "1"

EXTENSIONS: dict[str, str] = {
    ".py": "python", ".pyi": "python",
    ".cs": "csharp",
    ".ts": "typescript", ".tsx": "tsx", ".mts": "typescript", ".cts": "typescript",
    ".js": "javascript", ".jsx": "javascript", ".mjs": "javascript", ".cjs": "javascript",
    ".java": "java", ".go": "go", ".sql": "sql",
    ".rb": "ruby", ".rs": "rust", ".kt": "kotlin", ".swift": "swift", ".php": "php",
    ".c": "c", ".h": "c", ".cpp": "cpp", ".hpp": "cpp", ".cc": "cpp", ".scala": "scala",
    ".sh": "bash", ".ps1": "powershell", ".yaml": "yaml", ".yml": "yaml", ".toml": "toml",
    ".json": "json", ".xml": "xml", ".gradle": "groovy", ".tf": "hcl",
}


@dataclass
class _Grammar:
    symbols: set[str]                       # top-level nodes that become elements
    classes: set[str] = field(default_factory=set)
    methods: set[str] = field(default_factory=set)
    containers: set[str] = field(default_factory=set)  # walked through, not emitted
    imports: set[str] = field(default_factory=set)
    body_fields: tuple[str, ...] = ("body",)


GRAMMARS: dict[str, _Grammar] = {
    "python": _Grammar(
        symbols={"function_definition", "class_definition"},
        classes={"class_definition"}, methods={"function_definition"},
        containers={"decorated_definition"},
        imports={"import_statement", "import_from_statement", "future_import_statement"},
    ),
    "csharp": _Grammar(
        symbols={"class_declaration", "interface_declaration", "struct_declaration",
                 "record_declaration", "enum_declaration", "delegate_declaration"},
        classes={"class_declaration", "interface_declaration", "struct_declaration", "record_declaration"},
        methods={"method_declaration", "constructor_declaration", "property_declaration",
                 "operator_declaration", "indexer_declaration"},
        containers={"namespace_declaration", "file_scoped_namespace_declaration", "declaration_list"},
        imports={"using_directive"},
    ),
    "typescript": _Grammar(
        symbols={"function_declaration", "class_declaration", "abstract_class_declaration",
                 "interface_declaration", "type_alias_declaration", "enum_declaration",
                 "lexical_declaration"},
        classes={"class_declaration", "abstract_class_declaration"},
        methods={"method_definition", "public_field_definition", "method_signature"},
        containers={"export_statement"},
        imports={"import_statement"},
    ),
    "java": _Grammar(
        symbols={"class_declaration", "interface_declaration", "enum_declaration", "record_declaration"},
        classes={"class_declaration", "interface_declaration", "enum_declaration", "record_declaration"},
        methods={"method_declaration", "constructor_declaration"},
        imports={"import_declaration", "package_declaration"},
    ),
    "go": _Grammar(
        symbols={"function_declaration", "method_declaration", "type_declaration"},
        imports={"import_declaration", "package_clause"},
    ),
}
GRAMMARS["tsx"] = GRAMMARS["typescript"]
GRAMMARS["javascript"] = _Grammar(
    symbols={"function_declaration", "class_declaration", "lexical_declaration", "generator_function_declaration"},
    classes={"class_declaration"}, methods={"method_definition", "field_definition"},
    containers={"export_statement"}, imports={"import_statement"},
)


def language_for(path: str) -> str | None:
    return EXTENSIONS.get(PurePosixPath(path).suffix.lower())


def _text(source: bytes, node) -> str:
    return source[node.start_byte:node.end_byte].decode("utf-8", errors="replace")


def _name(source: bytes, node) -> str | None:
    named = node.child_by_field_name("name")
    if named is not None:
        return _text(source, named)
    # lexical_declaration (const x = () => …): the declarator holds the name.
    for child in node.children:
        if child.type == "variable_declarator":
            inner = child.child_by_field_name("name")
            if inner is not None:
                return _text(source, inner)
    # Go's type_declaration wraps a type_spec.
    for child in node.children:
        if child.type == "type_spec":
            inner = child.child_by_field_name("name")
            if inner is not None:
                return _text(source, inner)
    return None


def _lines(node) -> tuple[int, int]:
    return node.start_point[0] + 1, node.end_point[0] + 1


def _is_docstring(node) -> bool:
    # Grammar releases differ: a docstring is either a bare `string` node or an
    # `expression_statement` wrapping one.
    if node.type == "string":
        return True
    return node.type == "expression_statement" and bool(node.children) and node.children[0].type == "string"


def _docstring(language: str, source: bytes, body) -> str | None:
    if language != "python" or body is None:
        return None
    for child in body.children:
        if _is_docstring(child):
            return _text(source, child).strip("\"' \n")
        if child.type != "comment":
            return None
    return None


def _signature(source: bytes, node, body) -> str:
    """Everything before the body: decorators, modifiers, name, bases."""
    if body is None:
        return _text(source, node).split("\n", 1)[0]
    return source[node.start_byte:body.start_byte].decode("utf-8", errors="replace").rstrip(" {:\n")


def extract(document_id: str, content_hash: str, content_type: str, raw: bytes, path: str) -> DocumentModel:
    text = decode_text(raw)
    language = language_for(path) or "text"
    grammar = GRAMMARS.get(language)
    elements: list[Element] = []

    def add(body: str, kind: str, name: str | None, rng: tuple[int, int], **attributes) -> None:
        elements.append(Element(
            id=element_id(document_id, len(elements)),
            type="code", text=body, language=language,
            provenance=Provenance(file_path=path, line_range=rng),
            attributes={"symbol_kind": kind, "symbol_name": name, **attributes},
        ))

    source = text.encode("utf-8")
    tree = None
    if grammar is not None:
        try:
            from tree_sitter_language_pack import get_parser
            tree = get_parser(language).parse(source)
        except Exception:  # noqa: BLE001 — a missing grammar degrades to whole-file
            tree = None

    if tree is None:
        add(text.rstrip(), "file", PurePosixPath(path).name, (1, text.count("\n") + 1))
        return _model(document_id, content_hash, content_type, language, path, elements, parsed=False)

    header_parts: list[str] = []
    header_range: list[int] = []
    loose: list = []  # top-level statements that are not symbols, e.g. a script's main code

    def flush_loose() -> None:
        if not loose:
            return
        body = source[loose[0].start_byte:loose[-1].end_byte].decode("utf-8", errors="replace").strip()
        if body:
            add(body, "module_code", None, (_lines(loose[0])[0], _lines(loose[-1])[1]))
        loose.clear()

    def visit(node) -> None:
        for child in node.children:
            kind = child.type
            if kind in grammar.containers:
                # export_statement / decorated_definition wrap the real symbol.
                inner = [c for c in child.children if c.type in grammar.symbols]
                if inner and kind in ("export_statement", "decorated_definition"):
                    flush_loose()
                    emit(inner[0], outer=child)
                else:
                    visit(child)
                continue
            if kind in grammar.imports:
                # Flushed first, so a run of loose statements never spans the imports.
                flush_loose()
                header_parts.append(_text(source, child))
                header_range.extend(_lines(child))
                continue
            if kind in grammar.symbols:
                flush_loose()
                emit(child)
                continue
            if kind in ("comment", "{", "}", ";", "namespace", "identifier", "qualified_name"):
                continue
            if language == "python" and _is_docstring(child) and not elements and not loose \
                    and not header_parts:
                header_parts.insert(0, _text(source, child))  # module docstring
                header_range.extend(_lines(child))
                continue
            loose.append(child)
        flush_loose()

    def emit(node, outer=None) -> None:
        span = outer or node
        name = _name(source, node)
        body = node.child_by_field_name("body")
        attributes: dict = {}
        if node.type in grammar.classes:
            methods = []
            container = body if body is not None else node
            for member in container.children:
                target = member
                if member.type == "decorated_definition":
                    target = next((c for c in member.children if c.type in grammar.methods), member)
                if target.type in grammar.methods:
                    methods.append({
                        "name": _name(source, target),
                        "kind": target.type,
                        "line_range": list(_lines(member)),
                        "text": _text(source, member),
                    })
            attributes = {
                "signature": _signature(source, node, body),
                "docstring": _docstring(language, source, body),
                "methods": methods,
            }
        add(_text(source, span), node.type, name, _lines(span), **attributes)

    visit(tree.root_node)

    if header_parts:
        elements.insert(0, Element(
            id=element_id(document_id, len(elements)),
            type="code", text="\n".join(header_parts), language=language,
            provenance=Provenance(file_path=path, line_range=(min(header_range), max(header_range))),
            attributes={"symbol_kind": "file_header", "symbol_name": None},
        ))
        # Re-number so ids stay ordinal.
        for index, element in enumerate(elements):
            element.id = element_id(document_id, index)

    if not elements:
        add(text.rstrip(), "file", PurePosixPath(path).name, (1, text.count("\n") + 1))

    return _model(document_id, content_hash, content_type, language, path, elements, parsed=True)


def _model(document_id, content_hash, content_type, language, path, elements, parsed) -> DocumentModel:
    return DocumentModel(
        document_id=document_id, content_hash=content_hash, content_type=content_type,
        content_family="code", title=PurePosixPath(path).name, elements=elements,
        metadata={"programming_language": language, "path": path},
        extraction={"extractor": NAME, "version": VERSION, "parsed": parsed},
    )
