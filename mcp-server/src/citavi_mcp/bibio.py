"""RIS and BibTeX import/export without extra dependencies.

parse_* return parameter dicts for the bridge method references.create; to_bibtex writes
references from the bridge method project.export.
"""

from __future__ import annotations

import re
from typing import Any

from .crossref import choose_type

# RIS / BibTeX type -> Crossref-like type name understood by crossref.choose_type
RIS_TYPES = {
    "JOUR": "journal-article",
    "JFULL": "journal-article",
    "MGZN": "journal-article",
    "EJOUR": "journal-article",
    "BOOK": "book",
    "EBOOK": "book",
    "EDBOOK": "edited-book",
    "CHAP": "book-chapter",
    "ECHAP": "book-chapter",
    "CONF": "proceedings",
    "CPAPER": "proceedings-article",
    "THES": "dissertation",
    "RPRT": "report",
    "PAT": "patent",
    "STAND": "standard",
    "ELEC": "posted-content",
    "WEB": "posted-content",
    "NEWS": "journal-article",
    "UNPB": "posted-content",
}
BIB_TYPES = {
    "article": "journal-article",
    "book": "book",
    "inbook": "book-chapter",
    "incollection": "book-chapter",
    "inproceedings": "proceedings-article",
    "conference": "proceedings-article",
    "proceedings": "proceedings",
    "phdthesis": "dissertation",
    "mastersthesis": "dissertation",
    "techreport": "report",
    "manual": "report",
    "misc": "posted-content",
    "online": "posted-content",
    "unpublished": "posted-content",
    "patent": "patent",
}


def _person(raw: str) -> dict[str, str] | None:
    raw = raw.strip().strip("{}")
    if not raw:
        return None
    if "," in raw:
        last, first = raw.split(",", 1)
        return {"lastName": last.strip(), "firstName": first.strip()}
    parts = raw.split()
    return {"lastName": parts[-1], "firstName": " ".join(parts[:-1])}


def _params(kind: str, f: dict[str, Any], available: set[str] | None) -> dict[str, Any]:
    fields = {
        "Title": f.get("title"),
        "Subtitle": f.get("subtitle"),
        "Year": f.get("year"),
        "Doi": f.get("doi"),
        "Isbn": f.get("isbn"),
        "Volume": f.get("volume"),
        "Number": f.get("number"),
        "PageRange": f.get("pages"),
        "PlaceOfPublication": f.get("place"),
        "Edition": f.get("edition"),
        "OnlineAddress": f.get("url"),
        "Language": f.get("language"),
        "Notes": f.get("notes"),
    }
    if f.get("container") and kind not in ("journal-article",):
        fields["Notes"] = "; ".join(x for x in (fields.get("Notes"), f"Erschienen in: {f['container']}") if x)
    params: dict[str, Any] = {
        "type": choose_type(kind, available),
        "fields": {k: v for k, v in fields.items() if v},
        "authors": f.get("authors") or [],
        "editors": f.get("editors") or [],
        "abstract": f.get("abstract"),
        "keywords": f.get("keywords") or [],
    }
    if kind == "journal-article" and f.get("container"):
        params["periodical"] = f["container"]
    if f.get("publisher"):
        params["publishers"] = [f["publisher"]]
    return {k: v for k, v in params.items() if v}


def parse_ris(text: str, available_types: set[str] | None = None) -> list[dict[str, Any]]:
    records, current, kind = [], None, None
    for line in text.splitlines():
        m = re.match(r"^([A-Z][A-Z0-9])  -\s?(.*)$", line.strip("﻿"))
        if not m:
            continue
        tag, value = m.group(1), m.group(2).strip()
        if tag == "TY":
            current, kind = {"authors": [], "editors": [], "keywords": []}, RIS_TYPES.get(value.upper(), "other")
            continue
        if current is None:
            continue
        if tag == "ER":
            records.append(_params(kind, current, available_types))
            current = None
            continue
        if tag in ("AU", "A1"):
            if p := _person(value):
                current["authors"].append(p)
        elif tag in ("ED", "A2", "A3") and kind != "journal-article":
            if p := _person(value):
                current["editors"].append(p)
        elif tag in ("TI", "T1") and "title" not in current:
            current["title"] = value
        elif tag in ("T2", "JO", "JF", "JA", "BT") and "container" not in current:
            current["container"] = value
        elif tag in ("PY", "Y1", "DA") and "year" not in current:
            if y := re.search(r"\d{4}", value):
                current["year"] = y.group(0)
        elif tag == "SP":
            current["pages"] = value
        elif tag == "EP" and current.get("pages"):
            current["pages"] = f"{current['pages']}-{value}"
        elif tag == "VL":
            current["volume"] = value
        elif tag == "IS":
            current["number"] = value
        elif tag == "DO":
            current["doi"] = value
        elif tag == "SN":
            current["isbn"] = value
        elif tag == "UR" and "url" not in current:
            current["url"] = value
        elif tag in ("AB", "N2"):
            current["abstract"] = value
        elif tag == "KW":
            current["keywords"].append(value)
        elif tag == "PB":
            current["publisher"] = value
        elif tag == "CY":
            current["place"] = value
        elif tag == "ET":
            current["edition"] = value
        elif tag == "LA":
            current["language"] = value
        elif tag == "N1":
            current["notes"] = value
    return records


def _bib_entries(text: str):
    i = 0
    while (start := text.find("@", i)) >= 0:
        m = re.match(r"@(\w+)\s*\{\s*([^,\s]*)\s*,", text[start:])
        if not m:
            i = start + 1
            continue
        depth, j = 1, start + m.end()
        body_start = j
        while j < len(text) and depth:
            depth += {"{": 1, "}": -1}.get(text[j], 0)
            j += 1
        yield m.group(1).lower(), m.group(2), text[body_start : j - 1]
        i = j


def _bib_fields(body: str) -> dict[str, str]:
    fields, i = {}, 0
    while True:
        m = re.compile(r"\s*([A-Za-z][\w-]*)\s*=\s*").match(body, i)
        if not m:
            break
        name, i = m.group(1).lower(), m.end()
        if i < len(body) and body[i] in '{"':
            close = "}" if body[i] == "{" else '"'
            depth, j = 1, i + 1
            while j < len(body) and depth:
                if close == "}":
                    depth += {"{": 1, "}": -1}.get(body[j], 0)
                elif body[j] == '"':
                    depth = 0
                j += 1
            value, i = body[i + 1 : j - 1], j
        else:
            m2 = re.compile(r"[^,]*").match(body, i)
            value, i = m2.group(0).strip(), m2.end()
        fields[name] = re.sub(r"\s+", " ", value.replace("{", "").replace("}", "")).strip()
        comma = body.find(",", i)
        if comma < 0:
            break
        i = comma + 1
    return fields


def parse_bibtex(text: str, available_types: set[str] | None = None) -> list[dict[str, Any]]:
    records = []
    for kind, key, body in _bib_entries(text):
        if kind in ("comment", "string", "preamble"):
            continue
        f = _bib_fields(body)
        data: dict[str, Any] = {
            "title": f.get("title"),
            "subtitle": f.get("subtitle"),
            "year": (re.search(r"\d{4}", f.get("year") or f.get("date") or "") or [None])[0],
            "doi": f.get("doi"),
            "isbn": f.get("isbn"),
            "volume": f.get("volume"),
            "number": f.get("number") or f.get("issue"),
            "pages": (f.get("pages") or "").replace("--", "-") or None,
            "url": f.get("url"),
            "abstract": f.get("abstract"),
            "publisher": f.get("publisher") or f.get("institution") or f.get("school"),
            "place": f.get("address") or f.get("location"),
            "edition": f.get("edition"),
            "language": f.get("language"),
            "notes": f.get("note"),
            "container": f.get("journal") or f.get("journaltitle") or f.get("booktitle"),
            "authors": [p for p in map(_person, re.split(r"\s+and\s+", f.get("author", ""))) if p],
            "editors": [p for p in map(_person, re.split(r"\s+and\s+", f.get("editor", ""))) if p],
            "keywords": [k.strip() for k in re.split(r"[;,]", f.get("keywords", "")) if k.strip()],
        }
        params = _params(BIB_TYPES.get(kind, "other"), data, available_types)
        if key:
            params.setdefault("fields", {})["BibTeXKey"] = key
        records.append(params)
    return records


BIB_OUT = {
    "JournalArticle": "article",
    "Book": "book",
    "BookEdited": "book",
    "Contribution": "incollection",
    "ConferenceProceedings": "proceedings",
    "Thesis": "phdthesis",
    "UnpublishedWork": "unpublished",
    "InternetDocument": "online",
    "Patent": "patent",
    "Standard": "manual",
}


def _bib_escape(value: str) -> str:
    return value.replace("{", "").replace("}", "")


def to_bibtex(references: list[dict[str, Any]]) -> str:
    out, used = [], set()
    for r in references:
        f = r.get("fields") or {}
        first = (r.get("authors") or [{}])[0].get("lastName") or "o.A."
        key = f.get("BibTeXKey") or re.sub(r"\W+", "", f"{first}{r.get('year') or ''}") or "ref"
        base, n = key, 1
        while key in used:
            n += 1
            key = f"{base}{chr(96 + n)}"
        used.add(key)
        entry = {
            "author": " and ".join(
                f"{a['lastName']}, {a.get('firstName') or ''}".strip(", ") for a in r.get("authors") or []
            ),
            "editor": " and ".join(
                f"{a['lastName']}, {a.get('firstName') or ''}".strip(", ") for a in r.get("editors") or []
            ),
            "title": r.get("title"),
            "journal": r.get("periodical"),
            "year": r.get("year"),
            "volume": f.get("Volume"),
            "number": f.get("Number"),
            "pages": re.sub(r"\s*[-–—]+\s*", "--", f.get("PageRange") or "") or None,
            "publisher": ", ".join(r.get("publishers") or []) or None,
            "address": f.get("PlaceOfPublication"),
            "edition": f.get("Edition"),
            "doi": r.get("doi"),
            "isbn": f.get("Isbn"),
            "url": f.get("OnlineAddress"),
            "keywords": ", ".join(r.get("keywords") or []) or None,
        }
        lines = [f"@{BIB_OUT.get(r.get('typeKey') or '', 'misc')}{{{key},"]
        lines += [f"  {k} = {{{_bib_escape(str(v))}}}," for k, v in entry.items() if v]
        out.append("\n".join(lines).rstrip(",") + "\n}\n")
    return "\n".join(out)
