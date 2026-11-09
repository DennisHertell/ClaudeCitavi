"""Resolve DOIs via the public Crossref API and map them to Citavi reference parameters."""

from __future__ import annotations

import re
from typing import Any

import httpx

CROSSREF_URL = "https://api.crossref.org/works/{doi}"
USER_AGENT = "ClaudeCitavi/0.1 (https://github.com/DennisHertell/ClaudeCitavi)"

# Crossref "type" -> candidate Citavi ReferenceType names, best match first.
# The first name that exists in the installed Citavi version is used.
TYPE_MAP: dict[str, list[str]] = {
    "journal-article": ["JournalArticle"],
    "book": ["Book"],
    "monograph": ["Book"],
    "edited-book": ["BookEdited", "Book"],
    "book-chapter": ["Contribution"],
    "book-section": ["Contribution"],
    "proceedings-article": ["ContributionInProceedings", "Contribution"],
    "proceedings": ["ConferenceProceedings", "BookEdited", "Book"],
    "dissertation": ["Thesis"],
    "report": ["Report", "GreyLiterature", "UnpublishedWork"],
    "standard": ["Standard"],
    "patent": ["Patent"],
    "posted-content": ["UnpublishedWork", "InternetDocument"],
}
FALLBACK_TYPES = ["UnknownType", "UnpublishedWork", "JournalArticle"]


def choose_type(crossref_type: str, available: set[str] | None = None) -> str:
    candidates = TYPE_MAP.get(crossref_type, []) + FALLBACK_TYPES
    if available is None:
        return candidates[0]
    lowered = {a.lower(): a for a in available}
    for name in candidates:
        if name.lower() in lowered:
            return lowered[name.lower()]
    return "JournalArticle"


def normalize_doi(doi: str) -> str:
    doi = doi.strip()
    doi = re.sub(r"^(https?://)?(dx\.)?doi\.org/", "", doi, flags=re.I)
    doi = re.sub(r"^doi:\s*", "", doi, flags=re.I)
    return doi


def _first(values: Any) -> str | None:
    if isinstance(values, list):
        return values[0] if values else None
    return values


def _strip_jats(text: str | None) -> str | None:
    if not text:
        return None
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", text)).strip() or None


def fetch(doi: str, transport: httpx.BaseTransport | None = None) -> dict[str, Any]:
    doi = normalize_doi(doi)
    with httpx.Client(transport=transport, timeout=20.0, headers={"User-Agent": USER_AGENT}) as http:
        response = http.get(CROSSREF_URL.format(doi=doi))
    if response.status_code == 404:
        raise ValueError(f"DOI {doi} ist bei Crossref nicht bekannt.")
    response.raise_for_status()
    return response.json()["message"]


def to_reference_params(work: dict[str, Any], available_types: set[str] | None = None) -> dict[str, Any]:
    """Maps a Crossref work record to parameters for the bridge method references.create."""
    year = None
    for key in ("published-print", "published-online", "issued", "created"):
        parts = (work.get(key) or {}).get("date-parts") or [[None]]
        if parts and parts[0] and parts[0][0]:
            year = str(parts[0][0])
            break

    def persons(entries: list[dict[str, Any]] | None) -> list[dict[str, str]]:
        result = []
        for p in entries or []:
            last = p.get("family") or p.get("name")
            if last:
                result.append({"lastName": last, "firstName": p.get("given") or ""})
        return result

    fields: dict[str, str] = {
        "Title": _first(work.get("title")) or "",
        "Year": year,
        "Doi": work.get("DOI"),
        "Volume": work.get("volume"),
        "Number": work.get("issue"),
        "PageRange": work.get("page"),
        "OnlineAddress": work.get("URL"),
    }
    subtitle = _first(work.get("subtitle"))
    if subtitle:
        fields["Subtitle"] = subtitle
    isbn = _first(work.get("ISBN"))
    if isbn:
        fields["Isbn"] = isbn

    crossref_type = work.get("type", "")
    params: dict[str, Any] = {
        "type": choose_type(crossref_type, available_types),
        "fields": {k: v for k, v in fields.items() if v},
        "authors": persons(work.get("author")),
        "editors": persons(work.get("editor")),
        "abstract": _strip_jats(work.get("abstract")),
    }

    container = _first(work.get("container-title"))
    if container and crossref_type == "journal-article":
        params["periodical"] = container
    elif container:
        # Parent book/proceedings title; kept in notes until parent references are supported.
        params["fields"]["Notes"] = f"Erschienen in: {container}"

    if work.get("publisher") and crossref_type != "journal-article":
        params["publishers"] = [work["publisher"]]

    return {k: v for k, v in params.items() if v}
