#!/usr/bin/env python3
"""Fail-closed structural inspection for workflow credential values."""

from __future__ import annotations

import pathlib
import re
import sys
from typing import Iterable

try:
    import yaml
    from yaml.nodes import MappingNode, Node, ScalarNode, SequenceNode
except Exception as error:  # pragma: no cover - exercised by the wrapper
    print(f"YAML parser unavailable: {error}")
    raise SystemExit(2)


SENSITIVE_NAME = re.compile(
    r"(?i)(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)"
)
CONNECTION_VALUE = re.compile(
    r"(?i)\b(?:password|passwd|pwd)\s*=\s*(?P<value>[^;,\r\n]+)"
)
YAML_VALUE = re.compile(
    r"(?im)^\s*[\w.-]*(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)[\w.-]*\s*:\s*(?P<value>.+?)\s*(?:#.*)?$"
)
SCRIPT_ASSIGNMENT = re.compile(
    r"(?im)(?<![\w-])(?:\$env:[A-Za-z_][A-Za-z0-9_.-]*?(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)[A-Za-z0-9_.-]*|\$?(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)[A-Za-z0-9_.-]*|\$?[A-Za-z_][A-Za-z0-9_.-]*?(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)[A-Za-z0-9_.-]*)\s*=\s*(?P<value>[^;\r\n]+)"
)
SCRIPT_PATH = re.compile(
    r"(?<![A-Za-z0-9_.-])(?P<path>(?:\.{0,2}[\\/])?(?:build|scripts|\.githooks)[\\/][A-Za-z0-9_.-]+(?:[\\/][A-Za-z0-9_.-]+)*)"
)
ALLOWED_TAG_PREFIXES = ("tag:yaml.org,2002:", "!")


def is_sensitive_name(value: str) -> bool:
    return bool(SENSITIVE_NAME.search(value))


def is_runtime_value(value: str) -> bool:
    candidate = value.strip()
    while len(candidate) >= 2 and candidate[0] == candidate[-1] and candidate[0] in "\"'":
        candidate = candidate[1:-1].strip()

    if not candidate or candidate.lower() in {"null", "~"}:
        return True

    # GitHub expressions, including the prefix/suffix used to make CI service
    # passwords satisfy provider complexity requirements, are runtime values.
    expressions = re.findall(r"\$\{\{(.*?)\}\}", candidate, flags=re.DOTALL)
    if expressions:
        for expression in expressions:
            if re.search(r"['\"][^'\"]+['\"]", expression):
                return False
            if not re.search(
                r"(?:secrets|vars|github|inputs|env|steps|needs|matrix)\.",
                expression,
            ):
                return False
        return True

    # Shell/PowerShell indirection and generated test values are not literals.
    if re.search(r"\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*", candidate):
        return True
    if "$({" in candidate or "$([" in candidate or "$({" in candidate:
        return True
    if "$((" in candidate or "$([" in candidate or "$(`" in candidate:
        return True
    if re.search(r"(?i)\b(?:newguid|guid\.newguid|os\.environ|process\.env)\b", candidate):
        return True

    return False


def line_for(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def scalar_violation(value: str) -> bool:
    for pattern in (CONNECTION_VALUE, YAML_VALUE, SCRIPT_ASSIGNMENT):
        for match in pattern.finditer(value):
            if not is_runtime_value(match.group("value")):
                return True
    return False


def walk_node(
    node: Node,
    source: pathlib.Path,
    violations: list[str],
    scripts: set[pathlib.Path],
    path: str,
) -> None:
    if node.tag and not node.tag.startswith(ALLOWED_TAG_PREFIXES):
        violations.append(f"{source.as_posix()}:{node.start_mark.line + 1}: unsupported YAML tag")
        return

    if isinstance(node, ScalarNode):
        if scalar_violation(node.value):
            violations.append(f"{source.as_posix()}:{node.start_mark.line + 1}: literal credential scalar")
        for match in SCRIPT_PATH.finditer(node.value):
            candidate = match.group("path").replace("\\", "/")
            if candidate.startswith("./"):
                candidate = candidate[2:]
            scripts.add((source.parent.parent.parent / candidate).resolve())
        return

    if isinstance(node, SequenceNode):
        for index, child in enumerate(node.value):
            walk_node(child, source, violations, scripts, f"{path}[{index}]")
        return

    if isinstance(node, MappingNode):
        for index, (key, value) in enumerate(node.value):
            if not isinstance(key, ScalarNode):
                violations.append(f"{source.as_posix()}:{key.start_mark.line + 1}: unsupported YAML mapping key")
                continue

            is_permission_declaration = key.value.strip().lower() == "id-token"
            if is_sensitive_name(key.value) and not is_permission_declaration:
                if not isinstance(value, ScalarNode) or not is_runtime_value(value.value):
                    violations.append(f"{source.as_posix()}:{key.start_mark.line + 1}: literal credential mapping value")

            walk_node(key, source, violations, scripts, f"{path}.key{index}")
            walk_node(value, source, violations, scripts, f"{path}.{key.value}")


def read_yaml(path: pathlib.Path) -> Iterable[Node]:
    try:
        content = path.read_text(encoding="utf-8")
    except Exception as error:
        raise RuntimeError(f"cannot read workflow '{path}': {error}") from error

    try:
        documents = list(yaml.compose_all(content, Loader=yaml.SafeLoader))
    except Exception as error:
        raise RuntimeError(f"cannot parse workflow '{path}': {error}") from error

    if not documents:
        raise RuntimeError(f"workflow '{path}' contains no YAML document")
    for document in documents:
        if document is None:
            raise RuntimeError(f"workflow '{path}' contains an empty YAML document")
        yield document


def inspect_script(path: pathlib.Path, repository_root: pathlib.Path, violations: list[str]) -> None:
    try:
        relative = path.relative_to(repository_root)
    except ValueError as error:
        raise RuntimeError(f"workflow references script outside repository: '{path}'") from error

    if not path.is_file():
        raise RuntimeError(f"workflow-invoked script '{relative.as_posix()}' does not exist")

    try:
        content = path.read_text(encoding="utf-8")
    except Exception as error:
        raise RuntimeError(f"cannot inspect workflow-invoked script '{relative.as_posix()}': {error}") from error

    if scalar_violation(content):
        for pattern in (CONNECTION_VALUE, YAML_VALUE, SCRIPT_ASSIGNMENT):
            for match in pattern.finditer(content):
                if not is_runtime_value(match.group("value")):
                    violations.append(f"{relative.as_posix()}:{line_for(content, match.start())}: literal credential script value")


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: Parse-WorkflowCredentials.py <repository-root>")
        return 2

    repository_root = pathlib.Path(sys.argv[1]).resolve()
    workflow_root = repository_root / ".github" / "workflows"
    if not workflow_root.is_dir():
        print(f"workflow directory '{workflow_root}' does not exist")
        return 2

    violations: list[str] = []
    scripts: set[pathlib.Path] = set()
    workflows = sorted(path for path in workflow_root.rglob("*") if path.is_file() and path.suffix.lower() in {".yml", ".yaml"})
    if not workflows:
        print(f"workflow directory '{workflow_root}' contains no YAML workflows")
        return 2

    try:
        for workflow in workflows:
            for index, document in enumerate(read_yaml(workflow)):
                walk_node(document, workflow, violations, scripts, f"document[{index}]")

        for script in sorted(scripts):
            inspect_script(script, repository_root, violations)
    except RuntimeError as error:
        print(str(error))
        return 2

    if violations:
        for violation in sorted(set(violations)):
            print(violation)
        return 1

    print("Structural workflow credential inspection passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
