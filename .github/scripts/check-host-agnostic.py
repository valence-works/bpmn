#!/usr/bin/env python3
"""Fail if the repository names a specific workflow host outside an approved region.

This library must not depend on, reference, or assume any particular host. The architecture test
enforces that at the assembly level; a reference graph cannot catch a host's name in a doc comment, an
identifier, or a string literal, which is how a neutral library quietly drifts back towards the
codebase it came from.

There is one legitimate exception: prose that compares this library to alternatives, or tells a reader
to go use one. Naming them is the entire point of such a section, and refusing to would make the
comparison useless. So the exception is scoped rather than granted per file - wrap the region:

    <!-- host-agnostic-allow: comparing against alternative projects -->
    ... prose that may name hosts ...
    <!-- host-agnostic-allow-end -->

Everything outside such a region is still checked, including the rest of the same file.

Usage: check-host-agnostic.py [root]
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Hosts this library must not assume. The assembly-level allowlist in the architecture tests is the
# general guard; this is the prose backstop, so it names names.
FORBIDDEN = re.compile(r"elsa", re.IGNORECASE)

ALLOW_START = re.compile(r"host-agnostic-allow:", re.IGNORECASE)
ALLOW_END = re.compile(r"host-agnostic-allow-end", re.IGNORECASE)

SEARCH_ROOTS = ["src", "tests", "samples", "docs", "tools"]
ROOT_FILES = ["README.md", "CONTRIBUTING.md"]
EXTENSIONS = {".cs", ".csproj", ".props", ".md", ".yml", ".json", ".bpmn", ".slnx"}

# These two exist to enforce the rule, so they necessarily contain the token they search for.
SELF = {"check-host-agnostic.py", "HostAgnosticBoundaryTests.cs", "ci.yml"}


def offending_lines(path: Path) -> list[tuple[int, str]]:
    hits: list[tuple[int, str]] = []
    allowed = False

    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        # A file that will not decode cannot be checked. Report it rather than skipping silently: a
        # guard that passes on input it could not read is worse than no guard.
        return [(0, "<file is not valid UTF-8 and could not be checked>")]

    for number, line in enumerate(text.splitlines(), start=1):
        if ALLOW_START.search(line):
            allowed = True
            continue
        if ALLOW_END.search(line):
            allowed = False
            continue
        if not allowed and FORBIDDEN.search(line):
            hits.append((number, line.strip()))

    if allowed:
        hits.append((0, "<an unclosed host-agnostic-allow region reaches the end of the file>"))

    return hits


def candidates(root: Path):
    for folder in SEARCH_ROOTS:
        directory = root / folder
        if not directory.is_dir():
            continue
        for path in sorted(directory.rglob("*")):
            parts = set(path.parts)
            if "bin" in parts or "obj" in parts:
                continue
            if path.is_file() and path.suffix in EXTENSIONS and path.name not in SELF:
                yield path

    for name in ROOT_FILES:
        path = root / name
        if path.is_file():
            yield path


def main(argv: list[str]) -> int:
    root = Path(argv[1] if len(argv) > 1 else ".").resolve()

    failures: list[str] = []
    checked = 0

    for path in candidates(root):
        checked += 1
        for number, line in offending_lines(path):
            failures.append(f"{path.relative_to(root)}:{number}: {line}")

    if failures:
        print("This library must not name a specific host outside an approved region.")
        print("Wrap deliberate prose in <!-- host-agnostic-allow: reason --> ... <!-- host-agnostic-allow-end -->.")
        print()
        for failure in failures:
            print(f"  {failure}")
        return 1

    print(f"Checked {checked} file(s). No host-specific references outside approved regions.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
