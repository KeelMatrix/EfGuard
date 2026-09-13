#!/usr/bin/env python3
"""Fail-closed inspection of workflows and their bounded reachable file closure.

Coverage boundary: every tracked workflow under ``.github/workflows`` and the
tracked files reached from its ``run:``, ``shell:``, or ``uses:`` text, then
from those files' path-like tokens.  The closure follows at most
``MAX_REACHABLE_DEPTH`` reference edges and contains at most
``MAX_REACHABLE_FILES`` files.  A resolvable reference beyond either cap fails
closed instead of being silently skipped.

Files not reachable from workflow command text, dynamically constructed paths,
and base64 or otherwise encoded credential values are explicitly out of scope.
Literal credential-shaped values in the covered files are rejected, including
assignments, command arguments, connection strings, and YAML/JSON/XML/CSV/INI
style fields.  Runtime expressions and generated values remain allowed.

Attached short-option values are fail-closed: an attached value after the
short password switch is treated as credential-bearing unless the option is
one of the exact, case-insensitive non-credential allowlist entries
``-path``, ``-pathtype``, ``-project``, ``-properties``, ``-platform``,
``-publish``, ``-preview``, ``-parallel``, ``-port``, ``-provider``,
``-packagedirectory``, ``-passthru``, or ``-parent``.  The allowlist entries
may be followed by end of token, whitespace, ``:``, ``=``, or normal quoting
punctuation.  Option names or values assembled by shell concatenation, and
encoded values, cannot be evaluated by this static scanner and remain outside
its guarantee.
"""

from __future__ import annotations

import csv
import io
import pathlib
import re
import subprocess
import sys
from collections import deque
from typing import Iterable, Iterator

try:
    import yaml
    from yaml.nodes import MappingNode, Node, ScalarNode, SequenceNode
except Exception as error:  # pragma: no cover - exercised by the wrapper
    print(f"YAML parser unavailable: {error}")
    raise SystemExit(2)


MAX_REACHABLE_DEPTH = 3
MAX_REACHABLE_FILES = 64

SENSITIVE_NAME = re.compile(
    r"(?i)(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)"
)
SENSITIVE_NAME_TEXT = (
    r"(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)"
)
VALUE = r'''[^;\r\n]+'''
CONNECTION_VALUE = re.compile(
    rf"(?is)\b(?:password|passwd|pwd)\s*=\s*(?P<value>{VALUE})"
)
SENSITIVE_ASSIGNMENT = re.compile(
    rf"(?is)(?<![\w-])(?:"
    rf"\$env:[A-Za-z_][A-Za-z0-9_.-]*?{SENSITIVE_NAME_TEXT}[A-Za-z0-9_.-]*|"
    rf"\$?{SENSITIVE_NAME_TEXT}[A-Za-z0-9_.-]*|"
    rf"\$?[A-Za-z_][A-Za-z0-9_.-]*?{SENSITIVE_NAME_TEXT}[A-Za-z0-9_.-]*"
    rf")\s*=\s*(?P<value>{VALUE})"
)
KEY_VALUE = re.compile(
    rf"(?is)(?<![\w-])['\"]?(?P<key>[\w.-]*{SENSITIVE_NAME_TEXT}[\w.-]*)['\"]?"
    rf"\s*:\s*(?P<value>{VALUE})"
)
KEY_EQUALS_VALUE = re.compile(
    rf"(?is)(?<![\w-])['\"]?(?P<key>[\w.-]*{SENSITIVE_NAME_TEXT}[\w.-]*)['\"]?"
    rf"\s*=\s*(?P<value>{VALUE})"
)
SHORT_OPTION_PREFIX = "-" + "p"
COMMAND_ARGUMENT = re.compile(
    rf"(?is)(?<![\w-])(?:"
    rf"--{SENSITIVE_NAME_TEXT}|"
    rf"-{SENSITIVE_NAME_TEXT}|"
    rf"{SHORT_OPTION_PREFIX}"
    rf")(?:\s*[:=]\s*|\s+)(?P<value>{VALUE})"
)
NON_CREDENTIAL_SHORT_OPTIONS = frozenset(
    {
        "-path",
        "-pathtype",
        "-project",
        "-properties",
        "-platform",
        "-publish",
        "-preview",
        "-parallel",
        "-port",
        "-provider",
        "-packagedirectory",
        "-passthru",
        "-parent",
    }
)
NON_CREDENTIAL_SHORT_OPTION_SUFFIXES = "|".join(
    re.escape(option[2:])
    for option in sorted(NON_CREDENTIAL_SHORT_OPTIONS, key=len, reverse=True)
)
SHORT_OPTION_BOUNDARY = r"$|[\s:=`'\"\)\]\},;]"
ATTACHED_SHORT_COMMAND_ARGUMENT = re.compile(
    rf"(?is)(?<![\w-]){SHORT_OPTION_PREFIX}"
    rf"(?!(?:{NON_CREDENTIAL_SHORT_OPTION_SUFFIXES})(?={SHORT_OPTION_BOUNDARY}))"
    rf"(?P<value>(?=[^;\r\n\s:=]){VALUE})"
)
ATTACHED_SENSITIVE_COMMAND_ARGUMENT = re.compile(
    rf"(?is)(?<![\w-])(?:--{SENSITIVE_NAME_TEXT}|-{SENSITIVE_NAME_TEXT})"
    rf"(?P<value>(?=[^;\r\n\s:=]){VALUE})"
)
XML_ELEMENT_VALUE = re.compile(
    rf"(?is)<(?P<tag>(?:{SENSITIVE_NAME_TEXT}[\w:.-]*|[A-Za-z_][\w:.-]*{SENSITIVE_NAME_TEXT}[\w:.-]*))\b[^>]*>"
    rf"\s*(?P<value>.*?)\s*</(?P=tag)\s*>"
)

QUOTED_TOKEN = re.compile(r'''(?P<quote>["'])(?P<path>[^"'\r\n]+)(?P=quote)''')
KNOWN_FILE_SUFFIXES = frozenset(
    {
        ".config",
        ".csproj",
        ".csv",
        ".dll",
        ".ini",
        ".js",
        ".json",
        ".props",
        ".py",
        ".sh",
        ".sln",
        ".sql",
        ".targets",
        ".txt",
        ".xml",
        ".yaml",
        ".yml",
        ".ps1",
    }
)
SCRIPT_SUFFIXES = frozenset({".ps1", ".py", ".sh"})
ALLOWED_TAG_PREFIXES = ("tag:yaml.org,2002:", "!")


def is_sensitive_name(value: str) -> bool:
    return bool(SENSITIVE_NAME.search(value))


def is_runtime_value(value: str) -> bool:
    candidate = value.strip()
    while len(candidate) >= 2 and candidate[0] == candidate[-1] and candidate[0] in "\"'":
        candidate = candidate[1:-1].strip()

    if not candidate or re.match(
        r"(?i)^(?:null|~|true|false|yes|no|on|off|default)\W*$", candidate
    ):
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
    if "$(" in candidate or "$([" in candidate or "$(`" in candidate:
        return True
    if re.match(r"(?i)^(?:re\.compile|regex|new\s+regex|pattern)\b", candidate):
        return True
    if re.search(r"(?i)\b[A-Za-z_][A-Za-z0-9_.]*\s*\(", candidate) or "::" in candidate:
        return True
    if re.search(
        r"(?i)\b(?:newguid|guid\.newguid|uuidgen|os\.environ|process\.env)\b",
        candidate,
    ):
        return True

    return False


def line_for(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def credential_matches(text: str) -> Iterator[re.Match[str]]:
    for pattern in (
        CONNECTION_VALUE,
        SENSITIVE_ASSIGNMENT,
        KEY_VALUE,
        KEY_EQUALS_VALUE,
        COMMAND_ARGUMENT,
        ATTACHED_SHORT_COMMAND_ARGUMENT,
        ATTACHED_SENSITIVE_COMMAND_ARGUMENT,
        XML_ELEMENT_VALUE,
    ):
        yield from pattern.finditer(text)


def scalar_violation(value: str) -> bool:
    return any(
        not is_runtime_value(match.group("value"))
        for match in credential_matches(value)
    )


def walk_node(
    node: Node,
    source: pathlib.Path,
    violations: list[str],
    path_references: list[tuple[pathlib.Path, str]],
    path: str,
    collect_paths: bool = False,
) -> None:
    if node.tag and not node.tag.startswith(ALLOWED_TAG_PREFIXES):
        violations.append(f"{source.as_posix()}:{node.start_mark.line + 1}: unsupported YAML tag")
        return

    if isinstance(node, ScalarNode):
        if scalar_violation(node.value):
            violations.append(f"{source.as_posix()}:{node.start_mark.line + 1}: literal credential scalar")
        if collect_paths:
            path_references.append((source, node.value))
        return

    if isinstance(node, SequenceNode):
        for index, child in enumerate(node.value):
            walk_node(child, source, violations, path_references, f"{path}[{index}]", collect_paths)
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

            walk_node(key, source, violations, path_references, f"{path}.key{index}")
            collect_value_paths = collect_paths or key.value.strip().lower() in {"run", "shell", "uses"}
            walk_node(
                value,
                source,
                violations,
                path_references,
                f"{path}.{key.value}",
                collect_value_paths,
            )


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


def relative_path(path: pathlib.Path, repository_root: pathlib.Path) -> str:
    return path.relative_to(repository_root).as_posix()


def load_tracked_files(repository_root: pathlib.Path) -> set[str]:
    git_marker = repository_root / ".git"
    if git_marker.exists():
        try:
            result = subprocess.run(
                ["git", "-C", str(repository_root), "ls-files", "-z"],
                check=False,
                capture_output=True,
            )
        except Exception as error:
            raise RuntimeError(f"cannot enumerate tracked files: {error}") from error
        if result.returncode != 0:
            detail = result.stderr.decode("utf-8", errors="replace").strip()
            raise RuntimeError(f"cannot enumerate tracked files: {detail or 'git ls-files failed'}")
        return {
            item.decode("utf-8").replace("\\", "/")
            for item in result.stdout.split(b"\0")
            if item
        }

    # Synthetic contract repositories may not have a Git directory.  The
    # validator still uses the same existing-file boundary in that harness.
    return {
        relative_path(path, repository_root)
        for path in repository_root.rglob("*")
        if path.is_file() and ".git" not in path.parts
    }


def looks_path_like(candidate: str) -> bool:
    value = candidate.strip().strip("`'\"()[]{};,:")
    if not value or "\x00" in value or "://" in value:
        return False
    if re.match(r"(?i)^[A-Z]:[\\/]", value) or value.startswith("/"):
        return False
    return "/" in value or "\\" in value or pathlib.PurePosixPath(value).suffix.lower() in KNOWN_FILE_SUFFIXES


def path_candidates(text: str) -> Iterator[str]:
    quoted_spans: list[tuple[int, int]] = []
    for match in QUOTED_TOKEN.finditer(text):
        quoted_spans.append(match.span())
        candidate = match.group("path").strip()
        if looks_path_like(candidate):
            yield candidate

    for match in re.finditer(r'''(?<!\S)(?P<token>[^\s"'`]+)''', text):
        if any(start <= match.start() < end for start, end in quoted_spans):
            continue
        token = match.group("token").strip()
        candidates = [token]
        for separator in ("=", ":"):
            if separator in token and not re.match(r"(?i)^[A-Z]:[\\/]", token):
                candidates.append(token.rsplit(separator, 1)[1])
        for candidate in candidates:
            if looks_path_like(candidate):
                yield candidate


def resolve_reference(
    raw_path: str,
    source: pathlib.Path,
    repository_root: pathlib.Path,
    tracked_files: set[str],
) -> pathlib.Path | None:
    candidate_text = raw_path.strip().strip("`'\"()[]{};,")
    candidate_text = candidate_text.replace("\\", "/")

    if re.match(r"(?i)^[A-Z]:/", candidate_text) or candidate_text.startswith("/"):
        return None

    if candidate_text.startswith("$PSScriptRoot/"):
        bases = [source.parent]
        candidate_text = candidate_text[len("$PSScriptRoot/") :]
    elif candidate_text.startswith("${PSScriptRoot}/"):
        bases = [source.parent]
        candidate_text = candidate_text[len("${PSScriptRoot}/") :]
    elif candidate_text.startswith("$GITHUB_WORKSPACE/"):
        bases = [repository_root]
        candidate_text = candidate_text[len("$GITHUB_WORKSPACE/") :]
    else:
        bases = [source.parent, repository_root]

    for base in bases:
        candidate = (base / candidate_text).resolve()
        try:
            relative = relative_path(candidate, repository_root)
        except ValueError:
            continue

        if relative in tracked_files and candidate.is_file():
            return candidate

        # A local GitHub action is referenced by its directory.  Its manifest
        # is the deterministic file that starts the next closure edge.
        if candidate.is_dir():
            for manifest in ("action.yml", "action.yaml"):
                manifest_path = (candidate / manifest).resolve()
                try:
                    manifest_relative = relative_path(manifest_path, repository_root)
                except ValueError:
                    continue
                if manifest_relative in tracked_files and manifest_path.is_file():
                    return manifest_path

    return None


def unresolved_script_reference(
    raw_path: str,
    source: pathlib.Path,
    repository_root: pathlib.Path,
) -> pathlib.Path | None:
    """Return an obvious direct script path that is missing from the checkout."""
    candidate_text = raw_path.strip().strip("`'\"()[]{};,").replace("\\", "/")
    if not candidate_text.lower().endswith(tuple(SCRIPT_SUFFIXES)):
        return None
    if re.match(r"(?i)^[A-Z]:/", candidate_text) or candidate_text.startswith("/"):
        return None

    if candidate_text.startswith("$PSScriptRoot/"):
        bases = [source.parent]
        candidate_text = candidate_text[len("$PSScriptRoot/") :]
    elif candidate_text.startswith("${PSScriptRoot}/"):
        bases = [source.parent]
        candidate_text = candidate_text[len("${PSScriptRoot}/") :]
    elif candidate_text.startswith("$GITHUB_WORKSPACE/"):
        bases = [repository_root]
        candidate_text = candidate_text[len("$GITHUB_WORKSPACE/") :]
    else:
        bases = [source.parent, repository_root]

    for base in bases:
        candidate = (base / candidate_text).resolve()
        try:
            relative_path(candidate, repository_root)
        except ValueError:
            continue
        if not candidate.is_file():
            return candidate
    return None


def read_reachable_file(path: pathlib.Path, repository_root: pathlib.Path) -> str:
    relative = relative_path(path, repository_root)
    try:
        return path.read_text(encoding="utf-8")
    except Exception as error:
        raise RuntimeError(f"cannot inspect reachable file '{relative}': {error}") from error


def inspect_csv(content: str, relative: str, violations: list[str]) -> None:
    try:
        rows = list(csv.reader(io.StringIO(content, newline="")))
    except Exception as error:
        raise RuntimeError(f"cannot parse reachable CSV file '{relative}': {error}") from error
    if not rows:
        return

    sensitive_columns = {
        index for index, header in enumerate(rows[0]) if is_sensitive_name(header.strip())
    }
    for row_number, row in enumerate(rows[1:], start=2):
        for index in sensitive_columns:
            if index < len(row) and not is_runtime_value(row[index]):
                violations.append(f"{relative}:{row_number}: literal credential CSV value")


def inspect_reachable_file(
    path: pathlib.Path,
    repository_root: pathlib.Path,
    violations: list[str],
) -> None:
    content = read_reachable_file(path, repository_root)
    relative = relative_path(path, repository_root)
    for match in credential_matches(content):
        if not is_runtime_value(match.group("value")):
            violations.append(
                f"{relative}:{line_for(content, match.start())}: literal credential reachable value"
            )
    if path.suffix.lower() == ".csv":
        inspect_csv(content, relative, violations)


def collect_reachable_files(
    workflows: list[pathlib.Path],
    workflow_references: list[tuple[pathlib.Path, str]],
    repository_root: pathlib.Path,
    tracked_files: set[str],
) -> dict[pathlib.Path, int]:
    if len(workflows) > MAX_REACHABLE_FILES:
        raise RuntimeError(
            f"workflow file count exceeds reachable-file cap of {MAX_REACHABLE_FILES}"
        )

    references_by_source: dict[pathlib.Path, list[str]] = {}
    for source, text in workflow_references:
        references_by_source.setdefault(source, []).append(text)

    reachable = {workflow: 0 for workflow in workflows}
    queue: deque[tuple[pathlib.Path, int]] = deque((workflow, 0) for workflow in workflows)
    while queue:
        source, depth = queue.popleft()
        if source in references_by_source:
            references = references_by_source[source]
        else:
            references = [read_reachable_file(source, repository_root)]

        for reference_text in references:
            for raw_path in path_candidates(reference_text):
                candidate = resolve_reference(raw_path, source, repository_root, tracked_files)
                if candidate is None:
                    if source in workflows:
                        missing = unresolved_script_reference(raw_path, source, repository_root)
                        if missing is not None:
                            raise RuntimeError(
                                f"workflow-invoked script '{relative_path(missing, repository_root)}' does not exist"
                            )
                    continue
                if candidate in reachable:
                    continue
                if depth >= MAX_REACHABLE_DEPTH:
                    raise RuntimeError(
                        f"reachable file depth cap of {MAX_REACHABLE_DEPTH} exceeded by "
                        f"'{relative_path(candidate, repository_root)}'"
                    )
                if len(reachable) >= MAX_REACHABLE_FILES:
                    raise RuntimeError(
                        f"reachable-file cap of {MAX_REACHABLE_FILES} exceeded by "
                        f"'{relative_path(candidate, repository_root)}'"
                    )
                reachable[candidate] = depth + 1
                queue.append((candidate, depth + 1))

    return reachable


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
    workflow_references: list[tuple[pathlib.Path, str]] = []
    workflows = sorted(
        path
        for path in workflow_root.rglob("*")
        if path.is_file() and path.suffix.lower() in {".yml", ".yaml"}
    )
    if not workflows:
        print(f"workflow directory '{workflow_root}' contains no YAML workflows")
        return 2

    try:
        for workflow in workflows:
            for index, document in enumerate(read_yaml(workflow)):
                walk_node(
                    document,
                    workflow,
                    violations,
                    workflow_references,
                    f"document[{index}]",
                )

        tracked_files = load_tracked_files(repository_root)
        reachable = collect_reachable_files(
            workflows,
            workflow_references,
            repository_root,
            tracked_files,
        )
        for path in sorted(reachable):
            if path not in workflows:
                inspect_reachable_file(path, repository_root, violations)
    except RuntimeError as error:
        print(str(error))
        return 2

    if violations:
        for violation in sorted(set(violations)):
            print(violation)
        return 1

    print(
        "Structural workflow credential inspection passed "
        f"({len(reachable)} reachable file(s), depth cap {MAX_REACHABLE_DEPTH}, "
        f"file cap {MAX_REACHABLE_FILES})."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
