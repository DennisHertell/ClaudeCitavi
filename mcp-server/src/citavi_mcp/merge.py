"""Comparing and merging two Citavi projects (e.g. a student's shared cloud project into the user's project).

Pure functions here; the MCP tools in server.py call the bridge and use them.
Rules:
- A source reference matches a target reference by DOI, ISBN-13 or normalized title + year.
- A source knowledge item already exists in the target when its normalized text matches, or its
  normalized core statement and page range match.
- The target keeps its own category system: source categories are translated with a mapping
  {source path: target path}; unmapped categories are reported, never copied silently.
"""

from __future__ import annotations

import re
import unicodedata
from typing import Any


def norm_text(value: str | None) -> str:
    if not value:
        return ""
    value = value.lower()
    for umlaut, spelled in (("ä", "ae"), ("ö", "oe"), ("ü", "ue"), ("ß", "ss")):
        value = value.replace(umlaut, spelled)
    value = unicodedata.normalize("NFKD", value)
    return re.sub(r"[^0-9a-z]+", "", value)


def norm_doi(value: str | None) -> str | None:
    if not value:
        return None
    m = re.search(r"10\.\d{4,9}/\S+", value, re.IGNORECASE)
    return m.group(0).rstrip(".,;").lower() if m else None


def norm_isbn(value: str | None) -> str | None:
    if not value:
        return None
    digits = re.sub(r"[^0-9Xx]", "", value).upper()
    if len(digits) == 10:
        core = "978" + digits[:9]
        total = sum(int(c) * (1 if i % 2 == 0 else 3) for i, c in enumerate(core))
        digits = core + str((10 - total % 10) % 10)
    return digits if len(digits) == 13 else None


def title_key(ref: dict) -> str | None:
    title = norm_text(ref.get("title"))
    if len(title) < 12:
        return None
    return f"{title}|{ref.get('year') or ''}"


def reference_keys(ref: dict) -> list[tuple[str, str]]:
    keys = []
    if doi := norm_doi(ref.get("doi") or (ref.get("fields") or {}).get("Doi")):
        keys.append(("doi", doi))
    if isbn := norm_isbn(ref.get("isbn") or (ref.get("fields") or {}).get("Isbn")):
        keys.append(("isbn", isbn))
    if tk := title_key(ref):
        keys.append(("title+year", tk))
    return keys


def ki_keys(ki: dict) -> set[str]:
    keys = set()
    if text := norm_text(ki.get("text")):
        keys.add("t:" + text)
    if core := norm_text(ki.get("coreStatement")):
        keys.add(f"c:{core}|{ki.get('pageRange') or ''}")
    return keys


def compare(source: dict, target: dict) -> dict[str, Any]:
    """source/target: results of the bridge method project.export."""
    index: dict[tuple[str, str], dict] = {}
    for ref in target.get("references", []):
        for key in reference_keys(ref):
            index.setdefault(key, ref)

    new_refs, matched = [], []
    source_categories: dict[str, int] = {}
    for ref in source.get("references", []):
        for ki in ref.get("knowledgeItems", []):
            for c in ki.get("categories", []):
                source_categories[c] = source_categories.get(c, 0) + 1
        for c in ref.get("categories", []):
            source_categories.setdefault(c, 0)

        hit, how = None, None
        for key in reference_keys(ref):
            if key in index:
                hit, how = index[key], key[0]
                break
        if hit is None:
            new_refs.append(
                {
                    "sourceId": ref["id"],
                    "shortTitle": ref.get("shortTitle"),
                    "knowledgeItems": len(ref.get("knowledgeItems", [])),
                    "attachments": len(ref.get("attachments", [])),
                    "categories": ref.get("categories", []),
                }
            )
            continue

        existing = set()
        for ki in hit.get("knowledgeItems", []):
            existing |= ki_keys(ki)
        new_kis = [ki for ki in ref.get("knowledgeItems", []) if not (ki_keys(ki) & existing)]
        matched.append(
            {
                "sourceId": ref["id"],
                "targetId": hit["id"],
                "shortTitle": ref.get("shortTitle"),
                "matchedBy": how,
                "newKnowledgeItems": [
                    {
                        "id": ki["id"],
                        "type": ki.get("type"),
                        "coreStatement": ki.get("coreStatement"),
                        "pageRange": ki.get("pageRange"),
                        "categories": ki.get("categories", []),
                    }
                    for ki in new_kis
                ],
                "alreadyPresentKnowledgeItems": len(ref.get("knowledgeItems", [])) - len(new_kis),
                "targetHasAttachment": bool(hit.get("attachments")),
                "sourceHasAttachment": bool(ref.get("attachments")),
            }
        )

    return {
        "sourceProject": source.get("project"),
        "targetProject": target.get("project"),
        "newReferences": new_refs,
        "matchedReferences": matched,
        "sourceCategoriesInUse": dict(sorted(source_categories.items())),
        "targetCategoryPaths": category_paths(target.get("categories", [])),
        "summary": {
            "newReferences": len(new_refs),
            "matchedReferences": len(matched),
            "newKnowledgeItemsOnMatched": sum(len(m["newKnowledgeItems"]) for m in matched),
            "knowledgeItemsOnNewReferences": sum(r["knowledgeItems"] for r in new_refs),
        },
    }


def category_paths(tree: list[dict], prefix: str = "") -> list[str]:
    paths = []
    for node in tree:
        path = f"{prefix} > {node['name']}" if prefix else node["name"]
        paths.append(path)
        paths.extend(category_paths(node.get("children", []), path))
    return paths


def map_categories(categories: list[str], mapping: dict[str, str | list[str] | None]) -> tuple[list[str], list[str]]:
    """Translates source category paths. Returns (target paths, unmapped source paths).

    A mapping value of None or "" means: deliberately drop this category.
    """
    result, unmapped = [], []
    for c in categories:
        if c not in mapping:
            unmapped.append(c)
            continue
        value = mapping[c]
        if not value:
            continue
        for v in [value] if isinstance(value, str) else value:
            if v and v not in result:
                result.append(v)
    return result, unmapped
