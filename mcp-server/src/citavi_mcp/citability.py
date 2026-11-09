"""Checks whether a PDF passage may become a quotation (rules in docs/ZITIERREGELN.md).

A passage is citable only if it
1. is in the main text (not abstract, not front matter before the introduction, not the reference list), and
2. states the work's own finding: the sentence carries no citation of another source. Otherwise the
   primary source has to be found and quoted instead.
Heuristics, deliberately strict: a false alarm costs one look, a wrong quotation costs credibility.
"""

from __future__ import annotations

import re
from typing import Any

ABSTRACT_HEADING = re.compile(r"^\s*(abstract|zusammenfassung|summary|kurzfassung|executive summary)\b", re.I | re.M)
INTRO_HEADING = re.compile(
    r"^\s*(?:1\.?|I\.?)?\s*(introduction|einleitung|einführung|motivation|background)\b|^\s*1\.?\s+[A-ZÄÖÜ]",
    re.I | re.M,
)
REFERENCES_HEADING = re.compile(
    r"^\s*(?:\d+\.?\s*)?(references|bibliography|literatur|literaturverzeichnis|quellenverzeichnis|works cited)\s*$",
    re.I | re.M,
)
KEYWORDS_LINE = re.compile(r"^\s*(keywords|key words|schlüsselwörter|schlagwörter)\s*[:.]", re.I | re.M)

YEAR = r"(?:1[89]|20)\d{2}[a-z]?"
NAME = r"[A-ZÄÖÜ][\w'’\-]+"
CITATION_PATTERNS = [
    ("numeric", re.compile(r"\[\s*\d+(?:\s*[,;–-]\s*\d+)*\s*\]")),
    (
        "author-year",
        re.compile(rf"\(\s*(?:e\.g\.,?\s*|z\.\s?B\.\s*|vgl\.\s*|see\s*|cf\.\s*)?{NAME}[^()]{{0,80}}?,?\s*{YEAR}"),
    ),
    ("author (year)", re.compile(rf"{NAME}(?:\s+(?:et al\.|and|und|&)\s*{NAME}?)?\s*\(\s*{YEAR}")),
    (
        "vgl./cf.",
        re.compile(r"\b(vgl\.|cf\.|see also|siehe auch|zitiert nach|cited in|according to|nach Angaben von)\s", re.I),
    ),
    ("superscript", re.compile(r"[a-zäöüß\)\.,][¹²³⁴⁵⁶⁷⁸⁹⁰]+")),
]


def _norm(s: str) -> str:
    s = re.sub(r"(\w)-\s*\n\s*(\w)", r"\1\2", s)  # hyphenation at line end
    return re.sub(r"\s+", " ", s).strip()


def _key(s: str) -> str:
    return re.sub(r"[^0-9a-zäöüß]", "", s.lower())


def _locate(page_text: str, passage: str) -> tuple[int, int] | None:
    """Start/end of the passage in the normalized page text, tolerant to whitespace, hyphenation, punctuation."""
    text = _norm(page_text)
    want = _key(passage)
    if not want:
        return None
    # map key positions back to text positions
    positions = [i for i, ch in enumerate(text) if re.match(r"[0-9a-zäöüß]", ch.lower())]
    keyed = "".join(text[i].lower() for i in positions)
    at = keyed.find(want[: min(len(want), 400)])
    if at < 0:
        return None
    start = positions[at]
    end = positions[min(at + len(want), len(positions)) - 1] + 1
    return start, end


def _sentence(text: str, start: int, end: int) -> str:
    left = max(text.rfind(". ", 0, start), text.rfind("? ", 0, start), text.rfind("! ", 0, start))
    right_candidates = [i for i in (text.find(". ", end), text.find("? ", end), text.find("! ", end)) if i >= 0]
    right = min(right_candidates) + 1 if right_candidates else len(text)
    return text[left + 1 if left >= 0 else 0 : right].strip()


def check(pages: list[dict[str, Any]], passage: str, pdf_page: int | None = None) -> dict[str, Any]:
    """pages: output of the bridge method pdf.text (page, label, text), at least up to the passage's page."""
    normalized = [(p["page"], _norm(p.get("text") or "")) for p in pages]
    found_page, span = None, None
    for page, text in normalized:
        if pdf_page and page != pdf_page:
            continue
        span = _locate(text, passage)
        if span:
            found_page = page
            break
    if not found_page:
        return {"citable": None, "violations": ["Passage nicht im PDF-Text gefunden; Prüfung nicht möglich."]}

    text = dict(normalized)[found_page]
    start, end = span
    sentence = _sentence(text, start, end)
    violations: list[str] = []
    citations: list[str] = []

    raw_by_page = {p["page"]: p.get("text") or "" for p in pages}

    def norm_pos(page: int, raw_index: int) -> int:
        """Position in the normalized page text for an index in the raw text (headings need line starts)."""
        return len(_norm(raw_by_page[page][:raw_index]))

    # 1. Abstract and front matter: before the introduction heading on the first pages.
    intro_page, intro_pos = None, None
    for page in sorted(raw_by_page):
        m = INTRO_HEADING.search(raw_by_page[page]) if page <= 4 else None
        if m:
            intro_page, intro_pos = page, norm_pos(page, m.start())
            break
    abstract_heading = ABSTRACT_HEADING.search(raw_by_page[found_page])
    before_intro = intro_page is not None and (
        found_page < intro_page or (found_page == intro_page and start < intro_pos)
    )
    if abstract_heading and found_page <= 3 and before_intro:
        violations.append("Stelle steht im Abstract. Abstracts sind nicht zitierfähig.")
    elif before_intro:
        violations.append(
            "Stelle steht vor der Einleitung (Abstract oder Vorspann). Nur der Haupttext ist zitierfähig."
        )
    if KEYWORDS_LINE.search(sentence):
        violations.append("Stelle gehört zu den Schlüsselwörtern, nicht zum Haupttext.")

    # 2. Reference list: passage after a references heading.
    for page in sorted(raw_by_page):
        if page > found_page:
            break
        rh = REFERENCES_HEADING.search(raw_by_page[page])
        if rh and (page < found_page or norm_pos(page, rh.start()) < start):
            violations.append("Stelle steht im Literaturverzeichnis.")
            break

    # 3. Citations inside the sentence: not the work's own finding.
    for name, pattern in CITATION_PATTERNS:
        for m in pattern.finditer(sentence):
            citations.append(f"{name}: {m.group(0).strip()}")
    if citations:
        violations.append(
            "Der Satz ist mit einer fremden Quelle belegt (Sekundärzitat). Primärquelle ermitteln und dort zitieren."
        )

    label = next((p.get("label") for p in pages if p["page"] == found_page), None)
    printed = label or _printed_page_number(dict(normalized)[found_page])
    return {
        "citable": not violations,
        "pdfPage": found_page,
        "printedPage": printed,
        "sentence": sentence[:600],
        "citationsInSentence": citations,
        "violations": violations,
    }


def _printed_page_number(text: str) -> str | None:
    """Page number printed in the running header/footer, e.g. '4078' at the top of the page."""
    head = text[:120]
    tail = text[-80:]
    for chunk in (head, tail):
        m = re.search(r"(?<![\d.,])(\d{1,4})(?![\d.,%])", chunk)
        if m:
            return m.group(1)
    return None
