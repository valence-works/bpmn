#!/usr/bin/env python3
"""Flatten a nested docs/ tree into a GitHub wiki checkout.

The wiki is a flat namespace: it has no directories, and a page's name is its filename. A docs tree
that reads well in the repository therefore cannot be copied across verbatim - nested paths collapse
and every relative link breaks.

This script does three things:

1. Maps each ``docs/**/*.md`` file to a flat wiki page name, joining path segments with ``-``.
   ``docs/index.md`` becomes ``Home.md``, the wiki's landing page.
2. Rewrites every relative Markdown link so it points at the flattened name instead of the original
   relative path. Anchors are preserved; absolute and external links are left alone.
3. Generates ``_Sidebar.md`` from the directory structure, so the wiki keeps the shape of the docs
   tree even though the underlying pages are flat.

Usage: sync-wiki.py <docs-dir> <wiki-dir>
"""

from __future__ import annotations

import re
import shutil
import sys
from pathlib import Path

LINK_PATTERN = re.compile(r"(!?\[[^\]]*\])\(([^)]+)\)")

# Directories rendered as sidebar sections, in the order a reader should meet them. Anything not
# listed still syncs; it is simply appended after these, alphabetically.
SECTION_ORDER = ["", "guides", "concepts", "reference", "adr"]

SECTION_TITLES = {
    "": "Overview",
    "guides": "Guides",
    "concepts": "Concepts",
    "reference": "Reference",
    "adr": "Decision records",
}


def page_name(relative: Path) -> str:
    """Flatten a docs-relative path into a wiki page name (without extension)."""
    if relative.as_posix() == "index.md":
        return "Home"
    return "-".join(relative.with_suffix("").parts)


def title_for(relative: Path, text: str) -> str:
    """Prefer the document's own H1; fall back to a humanized filename."""
    for line in text.splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return relative.stem.replace("-", " ").replace("_", " ").title()


def rewrite_links(text: str, source: Path, mapping: dict[str, str]) -> str:
    """Point every relative .md link at its flattened wiki page."""

    def replace(match: re.Match[str]) -> str:
        label, target = match.group(1), match.group(2)

        if target.startswith(("http://", "https://", "mailto:", "#", "/")):
            return match.group(0)

        path_part, _, anchor = target.partition("#")
        if not path_part.endswith(".md"):
            return match.group(0)

        resolved = (source.parent / path_part).as_posix()
        # Normalize away any ./ and ../ segments.
        resolved = Path(resolved).resolve().as_posix()
        key = mapping.get(resolved)
        if key is None:
            return match.group(0)

        return f"{label}({key}{'#' + anchor if anchor else ''})"

    return LINK_PATTERN.sub(replace, text)


def build_sidebar(pages: list[tuple[Path, str, str]]) -> str:
    """Group pages by their top-level docs folder and render a nested list."""
    sections: dict[str, list[tuple[str, str]]] = {}
    for relative, name, title in pages:
        section = relative.parts[0] if len(relative.parts) > 1 else ""
        sections.setdefault(section, []).append((title, name))

    ordered = [s for s in SECTION_ORDER if s in sections]
    ordered += sorted(s for s in sections if s not in SECTION_ORDER)

    lines = ["### BPMN for .NET", ""]
    for section in ordered:
        entries = sorted(sections[section], key=lambda e: e[0].lower())
        heading = SECTION_TITLES.get(section, section.replace("-", " ").title())
        lines.append(f"**{heading}**")
        lines.append("")
        for title, name in entries:
            lines.append(f"- [[{title}|{name}]]")
        lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2

    docs_dir = Path(argv[1]).resolve()
    wiki_dir = Path(argv[2]).resolve()

    if not docs_dir.is_dir():
        print(f"docs directory not found: {docs_dir}", file=sys.stderr)
        return 1
    if not wiki_dir.is_dir():
        print(f"wiki directory not found: {wiki_dir}", file=sys.stderr)
        return 1

    sources = sorted(p for p in docs_dir.rglob("*.md"))
    if not sources:
        print("No markdown files found under docs/.", file=sys.stderr)
        return 1

    # Absolute source path -> flattened wiki page name, so links can be resolved in one pass.
    mapping = {p.resolve().as_posix(): page_name(p.relative_to(docs_dir)) for p in sources}

    # Remove pages the wiki carries but docs no longer does, so deletions propagate. Leave the wiki's
    # own git metadata alone.
    for existing in wiki_dir.glob("*.md"):
        existing.unlink()
    assets = wiki_dir / "assets"
    if assets.exists():
        shutil.rmtree(assets)

    pages: list[tuple[Path, str, str]] = []
    for source in sources:
        relative = source.relative_to(docs_dir)
        text = source.read_text(encoding="utf-8")
        name = mapping[source.resolve().as_posix()]
        pages.append((relative, name, title_for(relative, text)))
        (wiki_dir / f"{name}.md").write_text(rewrite_links(text, source, mapping), encoding="utf-8")

    # Carry images and other non-markdown assets across, preserving their relative layout.
    for asset in docs_dir.rglob("*"):
        if asset.is_file() and asset.suffix.lower() not in {".md"}:
            destination = wiki_dir / "assets" / asset.relative_to(docs_dir)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(asset, destination)

    (wiki_dir / "_Sidebar.md").write_text(build_sidebar(pages), encoding="utf-8")

    print(f"Synced {len(pages)} page(s) to the wiki.")
    for _, name, title in sorted(pages, key=lambda p: p[1]):
        print(f"  {name}.md  <-  {title}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
