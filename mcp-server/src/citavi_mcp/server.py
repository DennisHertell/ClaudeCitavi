"""MCP server exposing Citavi to Claude.

Every tool is a thin wrapper around one bridge RPC method (see docs/BRIDGE-PROTOKOLL.md).
The heavy lifting happens inside Citavi; this layer adds descriptions, input
validation and a few conveniences such as DOI import.
"""

from __future__ import annotations

from typing import Annotated, Any, Literal

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from . import bibio, citability, crossref, merge
from .bridge_client import BridgeClient, BridgeError

INSTRUCTIONS = """\
Werkzeuge für das gerade in Citavi geöffnete Projekt (Literaturverwaltung).

Arbeitsweise:
- Vor dem Anlegen immer erst mit search_references prüfen, ob die Quelle schon existiert (Dubletten vermeiden).
- IDs (GUIDs) stammen aus vorherigen Antworten; niemals IDs erfinden.
- Kategorien werden als Pfad angegeben, z. B. "Kapitel 2 > Methodik". Fehlende Kategorien werden angelegt.
- Wissenselement-Typen: direct (wörtliches Zitat), indirect (Paraphrase), summary, comment.
- Zitate aus PDFs: create_knowledge_item mit highlight_in_pdf=True markiert die Stelle im PDF und verknüpft sie,
  wie beim Markieren in Citavi. Bestehende Wissenselemente: highlight_in_pdf.
- Mehrere Projekte offen (z. B. lokales und Cloud-Projekt)? Mit list_open_projects/select_project wählen.
- Cloud-Projekte: PDFs, die anderswo hochgeladen wurden, mit list_attachments(only_missing=True) finden
  und mit download_attachments lokal ablegen.
- Kategorienbaum umbauen: rename_category, move_category, reorder_categories, merge_categories; danach
  mit set_categories nachsortieren. Vorher list_category_contents zeigen.
- Löschen und Zusammenführen (delete_*, merge_duplicates) nur nach Zustimmung, dann confirm=True.
- Anderes Projekt übernehmen (z. B. Hiwi): compare_projects, Kategorienzuordnung vorschlagen und freigeben lassen,
  merge_project mit dry_run=True, dann dry_run=False. Nie die Kategorienstruktur der Quelle übernehmen.
- Titel per ID: import_reference_by_identifier (DOI, ISBN, PMID, arXiv) bevorzugen.
- Antworten mit "warnings" bedeuten: der Eintrag wurde angelegt, aber einzelne Felder konnten nicht gesetzt werden.
  Diese Warnungen dem Nutzer mitteilen.
- Zitierregeln und Arbeitsablauf zum Auswerten einer Quelle stehen unten und sind verbindlich.
"""


def _rules_text() -> str:
    """docs/ZITIERREGELN.md is the single source of the citation rules; shipped with the repository."""
    from pathlib import Path

    for candidate in (
        Path(__file__).resolve().parents[3] / "docs" / "ZITIERREGELN.md",
        Path(__file__).resolve().parent / "ZITIERREGELN.md",
    ):
        if candidate.exists():
            return candidate.read_text(encoding="utf-8")
    return (
        "Zitierregeln (Kurzfassung, Datei docs/ZITIERREGELN.md nicht gefunden): nur Haupttext zitieren, nie Abstract, "
        "nie Sätze, die selbst mit einer fremden Quelle belegt sind; dann die Primärquelle ermitteln und dort zitieren."
    )


INSTRUCTIONS = INSTRUCTIONS + "\n" + _rules_text()

mcp = MCPServer("citavi", instructions=INSTRUCTIONS, version="0.4.1")
bridge = BridgeClient()

READ = ToolAnnotations(read_only_hint=True, open_world_hint=False)
WRITE = ToolAnnotations(read_only_hint=False, destructive_hint=False, open_world_hint=False)

CategoryPath = Annotated[
    str, Field(description='Kategorienpfad, Ebenen mit ">" getrennt, z. B. "Kapitel 2 > Methodik"')
]
Person = Annotated[str, Field(description='"Nachname, Vorname" oder "Vorname Nachname"')]


def _call(method: str, timeout: float | None = None, _project: str | None = None, **params: Any) -> Any:
    try:
        return bridge.call(method, timeout=timeout, _project=_project, **params)
    except BridgeError as exc:
        raise ToolError(str(exc)) from exc


# --- Status & discovery -------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def citavi_status() -> dict:
    """Prüft die Verbindung zu Citavi und liefert Name, Typ und Umfang des aktiven Projekts."""
    return _call("system.ping")


def _projects() -> dict:
    """Bridge >= 0.2 returns {open: [...], known: [...]}; older bridges a plain list of open projects."""
    result = _call("projects.list")
    if isinstance(result, list):
        return {"open": result, "known": []}
    return result or {"open": [], "known": []}


@mcp.tool(annotations=READ)
def list_open_projects() -> dict:
    """Listet die geöffneten Citavi-Projekte (open, das aktive markiert) und alle Projekte aus Citavis
    Projektliste (known), die sich mit open_project öffnen lassen."""
    return _projects()


@mcp.tool(annotations=WRITE)
def open_project(
    name: Annotated[str, Field(description="Projektname wie in list_open_projects unter known")],
) -> dict:
    """Öffnet ein Citavi-Projekt (lokal oder Cloud) ohne Klick in Citavi und wählt es für alle folgenden
    Werkzeuge aus. Funktioniert auch, wenn in Citavi nur das Startfenster offen ist."""
    result = _call("projects.open", timeout=240, name=name)
    bridge.project = name
    return {**(result or {}), "selectedProject": name}


@mcp.tool(annotations=WRITE)
def select_project(
    name: Annotated[
        str | None,
        Field(
            description="Projektname; leer = immer das zuletzt aktive Citavi-Fenster. Nicht geöffnete Projekte "
            "aus der Projektliste werden automatisch geöffnet."
        ),
    ] = None,
) -> dict:
    """Legt fest, mit welchem Citavi-Projekt alle folgenden Werkzeuge arbeiten."""
    if name:
        projects = _projects()
        open_names = [p.get("name") for p in projects.get("open") or []]
        match = next((n for n in open_names if n and n.lower() == name.lower()), None)
        if match is None:
            known = [p.get("name") for p in projects.get("known") or []]
            match = next((n for n in known if n and n.lower() == name.lower()), None)
            if match is None:
                raise ToolError(
                    f"Projekt '{name}' ist weder geöffnet noch in Citavis Projektliste. "
                    f"Bekannt: {', '.join(n for n in known + open_names if n)}"
                )
            _call("projects.open", timeout=240, name=match)
        name = match
    bridge.project = name or None
    return {"selectedProject": bridge.project or "(aktives Fenster)"}


@mcp.tool(annotations=READ)
def list_reference_types() -> list:
    """Listet die in Citavi verfügbaren Dokumenttypen (für create_reference, Feld `type`)."""
    return _call("references.types")


# --- References ---------------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def search_references(
    query: Annotated[str | None, Field(description="Suchbegriffe (Titel, Autor, Jahr, DOI, ISBN, Schlagwort)")] = None,
    category: CategoryPath | None = None,
    limit: Annotated[int, Field(ge=1, le=200)] = 25,
) -> dict:
    """Sucht Literatur im aktiven Citavi-Projekt. Alle Suchbegriffe müssen vorkommen."""
    return _call("references.search", query=query, categoryPath=[category] if category else None, limit=limit)


@mcp.tool(annotations=READ)
def get_reference(reference_id: str) -> dict:
    """Liefert alle Details eines Titels inkl. Kategorien, Schlagwörtern und Anhängen (Standorten)."""
    return _call("references.get", id=reference_id)


@mcp.tool(annotations=WRITE)
def create_reference(
    title: str,
    reference_type: Annotated[
        str, Field(description="Dokumenttyp, z. B. JournalArticle, Book, Contribution")
    ] = "JournalArticle",
    authors: list[Person] | None = None,
    editors: list[Person] | None = None,
    year: str | None = None,
    periodical: Annotated[str | None, Field(description="Zeitschrift (bei Zeitschriftenaufsätzen)")] = None,
    publishers: list[str] | None = None,
    abstract: str | None = None,
    keywords: list[str] | None = None,
    categories: list[CategoryPath] | None = None,
    fields: Annotated[
        dict[str, str] | None,
        Field(
            description="Weitere Citavi-Felder, z. B. {'Doi': '...', 'Volume': '12', 'PageRange': '1-10', "
            "'PlaceOfPublication': 'Aachen', 'Isbn': '...', 'Subtitle': '...'}"
        ),
    ] = None,
) -> dict:
    """Legt einen neuen Titel im aktiven Citavi-Projekt an. Vorher mit search_references auf Dubletten prüfen."""
    all_fields = dict(fields or {})
    all_fields["Title"] = title
    if year:
        all_fields["Year"] = year
    return _call(
        "references.create",
        type=reference_type,
        fields=all_fields,
        authors=authors,
        editors=editors,
        periodical=periodical,
        publishers=publishers,
        abstract=abstract,
        keywords=keywords,
        categories=categories,
    )


@mcp.tool(annotations=WRITE)
def update_reference(
    reference_id: str,
    fields: Annotated[
        dict[str, str] | None, Field(description="Zu ändernde Citavi-Felder, z. B. {'Year': '2021'}")
    ] = None,
    authors: Annotated[list[Person] | None, Field(description="Ersetzt die Autorenliste vollständig")] = None,
    editors: Annotated[list[Person] | None, Field(description="Ersetzt die Herausgeberliste vollständig")] = None,
    periodical: str | None = None,
    abstract: str | None = None,
    keywords: Annotated[list[str] | None, Field(description="Werden ergänzt")] = None,
    categories: Annotated[list[CategoryPath] | None, Field(description="Werden ergänzt")] = None,
) -> dict:
    """Ändert einen bestehenden Titel."""
    return _call(
        "references.update",
        id=reference_id,
        fields=fields,
        authors=authors,
        editors=editors,
        periodical=periodical,
        abstract=abstract,
        keywords=keywords,
        categories=categories,
    )


@mcp.tool(annotations=ToolAnnotations(read_only_hint=False, destructive_hint=False, open_world_hint=True))
def import_reference_by_doi(
    doi: str,
    categories: list[CategoryPath] | None = None,
    keywords: list[str] | None = None,
    allow_duplicate: bool = False,
) -> dict:
    """Holt die Metadaten zu einer DOI bei Crossref und legt den Titel in Citavi an.

    Ist die DOI im Projekt schon vorhanden, wird der bestehende Titel zurückgegeben (außer allow_duplicate=True).
    """
    doi = crossref.normalize_doi(doi)
    if not allow_duplicate:
        existing = _call("references.search", query=doi, limit=5)
        for item in existing.get("items", []):
            if (item.get("doi") or "").lower() == doi.lower():
                return {"status": "exists", "reference": item}

    try:
        work = crossref.fetch(doi)
    except Exception as exc:  # network or HTTP problems
        raise ToolError(f"Crossref-Abfrage fehlgeschlagen: {exc}") from exc

    types = {t.get("key") for t in _call("references.types") or [] if t.get("key")}
    params = crossref.to_reference_params(work, available_types=types or None)
    if categories:
        params["categories"] = categories
    if keywords:
        params["keywords"] = keywords
    result = _call("references.create", **params)
    return {"status": "created", **result}


# --- Categories & keywords ----------------------------------------------------------------------


@mcp.tool(annotations=READ)
def list_categories() -> list:
    """Liefert das Kategoriensystem des Projekts als Baum."""
    return _call("categories.list")


@mcp.tool(annotations=WRITE)
def create_category(path: CategoryPath) -> dict:
    """Legt eine Kategorie an (fehlende übergeordnete Kategorien werden mit angelegt)."""
    return _call("categories.create", path=path)


@mcp.tool(annotations=WRITE)
def assign_categories(
    categories: list[CategoryPath],
    reference_ids: list[str] | None = None,
    knowledge_item_ids: list[str] | None = None,
    remove: Annotated[bool, Field(description="True = Zuordnung entfernen statt hinzufügen")] = False,
) -> dict:
    """Ordnet Titel und/oder Wissenselemente Kategorien zu (oder entfernt die Zuordnung)."""
    return _call(
        "categories.assign",
        categories=categories,
        referenceIds=reference_ids,
        knowledgeItemIds=knowledge_item_ids,
        remove=remove,
    )


@mcp.tool(annotations=READ)
def list_keywords() -> list:
    """Listet alle Schlagwörter des Projekts."""
    return _call("keywords.list")


@mcp.tool(annotations=WRITE)
def assign_keywords(
    keywords: list[str],
    reference_ids: list[str] | None = None,
    knowledge_item_ids: list[str] | None = None,
    remove: bool = False,
) -> dict:
    """Vergibt Schlagwörter an Titel und/oder Wissenselemente (oder entfernt sie)."""
    return _call(
        "keywords.assign",
        keywords=keywords,
        referenceIds=reference_ids,
        knowledgeItemIds=knowledge_item_ids,
        remove=remove,
    )


# --- Knowledge items ----------------------------------------------------------------------------

KnowledgeType = Literal["direct", "indirect", "summary", "comment"]
CITABLE_TYPES = {"direct", "indirect", "summary"}
_PDF_TEXT_CACHE: dict[tuple[str | None, str], list[dict]] = {}


def _pdf_pages(reference_id: str, up_to_page: int | None = None) -> list[dict]:
    """Page texts of a reference's PDF (cached per project and reference for this server process)."""
    key = (bridge.project, reference_id)
    pages = _PDF_TEXT_CACHE.get(key)
    if pages is None or (up_to_page and max((p["page"] for p in pages), default=0) < up_to_page):
        result = _call("pdf.text", timeout=300, referenceId=reference_id)
        pages = result.get("pages", [])
        _PDF_TEXT_CACHE[key] = pages
    return pages


def _check_citability(reference_id: str, passage: str, pdf_page: int | None) -> dict:
    return citability.check(_pdf_pages(reference_id, pdf_page), passage, pdf_page)


@mcp.tool(annotations=READ)
def list_knowledge_items(
    reference_id: str | None = None,
    category: CategoryPath | None = None,
    query: str | None = None,
    limit: Annotated[int, Field(ge=1, le=500)] = 50,
) -> dict:
    """Listet Wissenselemente (Zitate, Zusammenfassungen, Kommentare), optional gefiltert."""
    return _call(
        "knowledgeItems.list",
        referenceId=reference_id,
        categoryPath=[category] if category else None,
        query=query,
        limit=limit,
    )


@mcp.tool(annotations=WRITE)
def create_knowledge_item(
    reference_id: str,
    text: str,
    knowledge_type: KnowledgeType = "direct",
    core_statement: Annotated[str | None, Field(description="Kernaussage (kurze Überschrift)")] = None,
    page_range: Annotated[str | None, Field(description='Seitenangabe, z. B. "12" oder "12-14"')] = None,
    categories: list[CategoryPath] | None = None,
    keywords: list[str] | None = None,
    highlight_in_pdf: Annotated[
        bool,
        Field(description="True = Textstelle im PDF des Titels markieren und mit dem Wissenselement verknüpfen"),
    ] = False,
    pdf_search_text: Annotated[
        str | None,
        Field(description="Text, der im PDF markiert wird, falls er vom Zitattext abweicht (z. B. bei Kürzungen)"),
    ] = None,
    pdf_page: Annotated[
        int | None, Field(description="Seite im PDF (PDF-Seitenzahl), um die Suche einzugrenzen")
    ] = None,
    groups: list[str] | None = None,
    citability_override: Annotated[
        str | None,
        Field(
            description="Nur mit ausdrücklicher Freigabe des Nutzers: Begründung, warum eine als nicht zitierfähig "
            "erkannte Stelle trotzdem angelegt wird. Sonst leer lassen."
        ),
    ] = None,
) -> dict:
    """Legt ein Wissenselement zu einem Titel an, auf Wunsch mit Markierung im PDF (wie Markieren in Citavi).

    Zitierregeln (docs/ZITIERREGELN.md) werden bei Zitaten und Zusammenfassungen mit PDF-Stelle automatisch geprüft:
    nur Haupttext, kein Abstract, kein mit fremder Quelle belegter Satz. Verstöße legen nichts an.
    Ohne page_range wird die gedruckte Seitenzahl der Stelle eingetragen.
    """
    search = pdf_search_text or text
    warnings: list[str] = []
    if highlight_in_pdf:
        found = _call("pdf.findText", timeout=180, referenceId=reference_id, text=search, page=pdf_page, limit=1)
        if not found.get("total"):
            raise ToolError(
                "Text im PDF nicht gefunden; nichts angelegt. Kürzere, exakte Passage (ohne Silbentrennung) "
                "als pdf_search_text angeben oder highlight_in_pdf=False."
            )
        hit_page = found["hits"][0]["page"]
        if knowledge_type in CITABLE_TYPES:
            verdict = _check_citability(reference_id, search, hit_page)
            if verdict.get("citable") is False and not citability_override:
                raise ToolError(
                    "Nicht zitierfähig, nichts angelegt: "
                    + " ".join(verdict["violations"])
                    + (
                        f" Belege im Satz: {'; '.join(verdict['citationsInSentence'])}."
                        if verdict.get("citationsInSentence")
                        else ""
                    )
                    + f' Satz: "{verdict.get("sentence", "")[:300]}"'
                )
            if verdict.get("citable") is False:
                warnings.append(f"Zitierregel bewusst übergangen: {citability_override}")
            if not page_range and verdict.get("printedPage"):
                page_range = verdict["printedPage"]
                warnings.append(
                    f"Seitenangabe {page_range} aus der gedruckten Seitenzahl übernommen (PDF-Seite {hit_page})."
                )
    elif knowledge_type in CITABLE_TYPES:
        warnings.append("Zitierfähigkeit nicht automatisch geprüft (keine PDF-Stelle angegeben).")

    created = _call(
        "knowledgeItems.create",
        referenceId=reference_id,
        type=knowledge_type,
        text=text,
        coreStatement=core_statement,
        pageRange=page_range,
        categories=categories,
        keywords=keywords,
        groups=groups,
    )
    if warnings:
        created.setdefault("warnings", []).extend(warnings)
    if highlight_in_pdf:
        ki_id = created["knowledgeItem"]["id"]
        try:
            created["highlight"] = _call(
                "pdf.highlight", timeout=180, knowledgeItemId=ki_id, text=search, page=pdf_page
            )
        except ToolError as exc:
            created.setdefault("warnings", []).append(f"Wissenselement angelegt, Markierung fehlgeschlagen: {exc}")
    return created


@mcp.tool(annotations=WRITE)
def highlight_in_pdf(
    knowledge_item_id: str,
    text: Annotated[str | None, Field(description="Zu markierender Text; leer = Text des Wissenselements")] = None,
    page: Annotated[int | None, Field(description="PDF-Seitenzahl, um die Suche einzugrenzen")] = None,
    occurrence: Annotated[int, Field(ge=1, description="Welcher Treffer, falls der Text mehrfach vorkommt")] = 1,
    color: Annotated[str | None, Field(description="Farbe als #RRGGBB, Standard Gelb")] = None,
) -> dict:
    """Markiert die Textstelle eines bestehenden Wissenselements im PDF und verknüpft beides (wie in Citavi)."""
    return _call(
        "pdf.highlight",
        timeout=180,
        knowledgeItemId=knowledge_item_id,
        text=text,
        page=page,
        occurrence=occurrence,
        color=color,
    )


@mcp.tool(annotations=READ)
def find_text_in_pdf(
    reference_id: str,
    text: str,
    page: int | None = None,
    limit: Annotated[int, Field(ge=1, le=100)] = 20,
) -> dict:
    """Sucht Text im PDF eines Titels und liefert Seiten und Fundstellen (ohne etwas zu ändern)."""
    return _call("pdf.findText", timeout=180, referenceId=reference_id, text=text, page=page, limit=limit)


@mcp.tool(annotations=WRITE)
def update_knowledge_item(
    knowledge_item_id: str,
    text: str | None = None,
    core_statement: str | None = None,
    page_range: str | None = None,
    categories: Annotated[list[CategoryPath] | None, Field(description="Werden ergänzt")] = None,
    keywords: Annotated[list[str] | None, Field(description="Werden ergänzt")] = None,
) -> dict:
    """Ändert ein bestehendes Wissenselement."""
    return _call(
        "knowledgeItems.update",
        id=knowledge_item_id,
        text=text,
        coreStatement=core_statement,
        pageRange=page_range,
        categories=categories,
        keywords=keywords,
    )


# --- Attachments --------------------------------------------------------------------------------


@mcp.tool(annotations=WRITE)
def attach_file(
    reference_id: str,
    path: Annotated[str, Field(description=r"Absoluter Windows-Pfad, z. B. C:\Users\...\paper.pdf")],
    mode: Annotated[
        Literal["copy", "move", "link"],
        Field(
            description="copy = Citavi kopiert die Datei ins Projekt (Cloud: lädt sie hoch), "
            "move = verschieben, link = nur verknüpfen"
        ),
    ] = "copy",
    notes: str | None = None,
) -> dict:
    """Hängt eine lokale Datei (z. B. PDF) an einen Titel an."""
    return _call("attachments.add", referenceId=reference_id, path=path, mode=mode, notes=notes)


@mcp.tool(annotations=READ)
def list_attachments(
    reference_ids: list[str] | None = None,
    category: CategoryPath | None = None,
    query: Annotated[str | None, Field(description="Filter auf Titel/Kurzbeleg")] = None,
    only_missing: Annotated[bool, Field(description="Nur Dateien, die auf diesem Rechner noch fehlen")] = False,
    only_pdf: bool = False,
    limit: Annotated[int, Field(ge=1, le=2000)] = 500,
) -> dict:
    """Listet Anhänge (PDFs, Dateien, Links) mit Speicherort: local, cloud, linked oder url.

    `availableLocally=false` bei storage=cloud heißt: Die Datei liegt nur in der Citavi Cloud,
    z. B. weil sie von einem anderen Rechner oder einer anderen Person hochgeladen wurde.
    """
    return _call(
        "attachments.list",
        referenceIds=reference_ids,
        categoryPath=[category] if category else None,
        query=query,
        onlyMissing=only_missing,
        onlyPdf=only_pdf,
        limit=limit,
    )


@mcp.tool(annotations=ToolAnnotations(read_only_hint=False, destructive_hint=False, open_world_hint=True))
def download_attachments(
    reference_ids: list[str] | None = None,
    location_ids: Annotated[
        list[str] | None, Field(description="Einzelne Anhänge (locationId aus list_attachments)")
    ] = None,
    category: CategoryPath | None = None,
    query: str | None = None,
    target_folder: Annotated[
        str | None,
        Field(
            description=r"Zielordner, z. B. C:\Users\...\Diss\PDFs. Leer = Standardordner aus bridge.json "
            r"(Dokumente\ClaudeCitavi\Downloads)"
        ),
    ] = None,
    naming: Annotated[
        Literal["citation", "original"],
        Field(description="citation = 'Autor Jahr - Titel.pdf', original = Dateiname wie in Citavi"),
    ] = "citation",
    only_pdf: bool = True,
    overwrite: bool = False,
    timeout_seconds: Annotated[int, Field(ge=10, le=3600)] = 300,
    limit: Annotated[int, Field(ge=1, le=1000)] = 200,
) -> dict:
    """Lädt Anhänge herunter und legt sie in einem lokalen Ordner ab, auch aus Citavi-Cloud-Projekten.

    Citavi lädt Cloud-Dateien selbst mit deinem Citavi-Login herunter (auch solche, die andere Personen
    oder andere Rechner hochgeladen haben); danach wird die Datei in den Zielordner kopiert.
    Ohne Auswahl (keine IDs, Kategorie oder Suche) werden alle PDFs des Projekts geladen.
    Ergebnis je Datei: downloaded, copied, already_there oder not_available (mit Hinweis).
    """
    return _call(
        "attachments.download",
        timeout=timeout_seconds + 30,
        referenceIds=reference_ids,
        locationIds=location_ids,
        categoryPath=[category] if category else None,
        query=query,
        targetFolder=target_folder,
        naming=naming,
        onlyPdf=only_pdf,
        overwrite=overwrite,
        timeoutSeconds=timeout_seconds,
        limit=limit,
    )


# --- Citation style -----------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def get_citation_style() -> dict:
    """Liefert den aktuellen Zitationsstil des Projekts."""
    return _call("citationStyle.get")


@mcp.tool(annotations=READ)
def list_citation_styles(
    query: Annotated[str | None, Field(description="Teil des Namens, z. B. 'DIN' oder 'APA'")] = None,
) -> dict:
    """Listet die Zitationsstile, die Citavi kennt (mitgeliefert, heruntergeladen, eigene)."""
    return _call("citationStyle.list", query=query)


@mcp.tool(annotations=WRITE)
def set_citation_style(
    name: Annotated[str | None, Field(description="Name (oder eindeutiger Teil) aus list_citation_styles")] = None,
    style_id: Annotated[str | None, Field(description="id aus list_citation_styles")] = None,
) -> dict:
    """Stellt den Zitationsstil des aktiven Projekts um."""
    if not name and not style_id:
        raise ToolError("Bitte name oder style_id angeben.")
    return _call("citationStyle.set", name=name, id=style_id)


# --- Diagnostics --------------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def describe_citavi_type(type_name: Annotated[str, Field(description="z. B. Reference, KnowledgeItem")]) -> dict:
    """Diagnose: zeigt Eigenschaften und Methoden eines Citavi-Typs (für Entwicklung und Fehlersuche)."""
    return _call("debug.describeType", typeName=type_name)


@mcp.tool(annotations=READ)
def find_citavi_members(pattern: Annotated[str, Field(description="Teil eines Namens, z. B. Download")]) -> dict:
    """Diagnose: durchsucht alle Citavi-Typen nach Eigenschaften/Methoden mit diesem Namensteil."""
    return _call("debug.findMembers", pattern=pattern)


@mcp.tool(annotations=READ)
def inspect_citavi_object(
    knowledge_item_id: str | None = None,
    location_id: Annotated[str | None, Field(description="locationId eines Anhangs aus list_attachments")] = None,
    depth: Annotated[int, Field(ge=1, le=5)] = 3,
) -> dict:
    """Diagnose: zeigt den internen Aufbau eines Wissenselements (inkl. Verknüpfungen zu PDF-Markierungen)
    oder eines Anhangs (inkl. Markierungen). Für die Entwicklung der PDF-Markierungsfunktion."""
    if not knowledge_item_id and not location_id:
        raise ToolError("Bitte knowledge_item_id oder location_id angeben.")
    return _call("debug.inspect", knowledgeItemId=knowledge_item_id, locationId=location_id, depth=depth)


# --- Category tree (Kategoriensystem umbauen) ---------------------------------------------------

DESTRUCTIVE = ToolAnnotations(read_only_hint=False, destructive_hint=True, open_world_hint=False)
Confirm = Annotated[
    bool,
    Field(description="Erst nach ausdrücklicher Zustimmung des Nutzers auf True setzen. Ohne True passiert nichts."),
]


@mcp.tool(annotations=WRITE)
def rename_category(path: CategoryPath, new_name: str) -> dict:
    """Benennt eine Kategorie um. Zuordnungen bleiben erhalten."""
    return _call("categories.rename", path=path, newName=new_name)


@mcp.tool(annotations=WRITE)
def move_category(
    path: CategoryPath,
    new_parent: Annotated[
        str | None,
        Field(description='Neue Oberkategorie als Pfad; "" = oberste Ebene; weglassen = gleiche Oberkategorie'),
    ] = None,
    after: Annotated[str | None, Field(description="Name der Geschwisterkategorie, hinter die verschoben wird")] = None,
    position: Literal["first", "last"] | None = None,
) -> dict:
    """Verschiebt eine Kategorie samt Unterkategorien, Titeln und Wissenselementen an eine andere Stelle im Baum
    (höher/tiefer stufen, andere Oberkategorie, Reihenfolge)."""
    return _call("categories.move", path=path, newParent=new_parent, after=after, position=position)


@mcp.tool(annotations=WRITE)
def reorder_categories(
    order: Annotated[list[str], Field(description="Namen der Unterkategorien in gewünschter Reihenfolge")],
    parent: Annotated[str | None, Field(description="Oberkategorie als Pfad; leer = oberste Ebene")] = None,
) -> list:
    """Legt die Reihenfolge der Unterkategorien fest (ändert die Gliederungsnummern)."""
    return _call("categories.reorder", parent=parent, order=order)


@mcp.tool(annotations=WRITE)
def sort_categories(parent: str | None = None, recursive: bool = False) -> list:
    """Sortiert Unterkategorien alphabetisch."""
    return _call("categories.sort", parent=parent, recursive=recursive)


@mcp.tool(annotations=WRITE)
def merge_categories(source: CategoryPath, target: CategoryPath) -> dict:
    """Führt eine Kategorie in eine andere über: Titel, Wissenselemente und Unterkategorien wandern mit,
    die Quellkategorie verschwindet."""
    return _call("categories.merge", source=source, target=target)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_category(
    path: CategoryPath,
    merge_into: Annotated[str | None, Field(description="Inhalte vorher in diese Kategorie überführen")] = None,
    confirm: Confirm = False,
) -> dict:
    """Löscht eine Kategorie. Titel und Wissenselemente bleiben erhalten, verlieren nur diese Kategorie
    (oder wandern nach merge_into)."""
    return _call("categories.delete", path=path, mergeInto=merge_into, confirm=confirm)


@mcp.tool(annotations=READ)
def list_category_contents(path: CategoryPath, include_subcategories: bool = False, limit: int = 200) -> dict:
    """Titel und Wissenselemente einer Kategorie, z. B. vor einem Umbau des Kategorienbaums."""
    return _call("categories.contents", path=path, includeSubcategories=include_subcategories, limit=limit)


@mcp.tool(annotations=WRITE)
def set_categories(
    categories: list[CategoryPath],
    reference_ids: list[str] | None = None,
    knowledge_item_ids: list[str] | None = None,
) -> dict:
    """Ersetzt die Kategorien von Titeln/Wissenselementen (neu einsortieren). Fehlende Kategorien werden angelegt."""
    return _call(
        "categories.set",
        categories=categories,
        referenceIds=reference_ids or [],
        knowledgeItemIds=knowledge_item_ids or [],
    )


@mcp.tool(annotations=READ)
def list_uncategorized(limit: int = 200) -> dict:
    """Titel und Wissenselemente ohne Kategorie."""
    return _call("categories.unassigned", limit=limit)


# --- Groups and keywords ------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def list_groups() -> list:
    """Alle Gruppen mit Anzahl der zugeordneten Titel und Wissenselemente."""
    return _call("groups.list")


@mcp.tool(annotations=WRITE)
def assign_groups(
    groups: list[str],
    reference_ids: list[str] | None = None,
    knowledge_item_ids: list[str] | None = None,
    remove: bool = False,
) -> dict:
    """Ordnet Titel/Wissenselemente Gruppen zu (fehlende Gruppen werden angelegt) oder entfernt sie (remove=True)."""
    return _call(
        "groups.assign",
        groups=groups,
        referenceIds=reference_ids or [],
        knowledgeItemIds=knowledge_item_ids or [],
        remove=remove,
    )


@mcp.tool(annotations=READ)
def keyword_usage() -> list:
    """Alle Schlagwörter mit Anzahl der zugeordneten Titel und Wissenselemente."""
    return _call("keywords.usage")


@mcp.tool(annotations=WRITE)
def rename_keyword_or_group(kind: Literal["keyword", "group"], name: str, new_name: str) -> dict:
    """Benennt ein Schlagwort oder eine Gruppe um."""
    return _call(f"{kind}s.rename", name=name, newName=new_name)


@mcp.tool(annotations=WRITE)
def merge_keywords_or_groups(kind: Literal["keyword", "group"], sources: list[str], target: str) -> dict:
    """Führt Schlagwörter oder Gruppen zusammen (Zuordnungen wandern zum Ziel, Quellen verschwinden)."""
    return _call(f"{kind}s.merge", sources=sources, target=target)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_keywords_or_groups(kind: Literal["keyword", "group"], names: list[str], confirm: Confirm = False) -> dict:
    """Löscht Schlagwörter oder Gruppen; die Zuordnungen entfallen, Titel und Wissenselemente bleiben."""
    return _call(f"{kind}s.delete", names=names, confirm=confirm)


# --- Thoughts and tasks -------------------------------------------------------------------------


@mcp.tool(annotations=READ)
def list_thoughts(query: str | None = None, limit: int = 100) -> dict:
    """Gedanken (Wissenselemente ohne Titel)."""
    return _call("thoughts.list", query=query, limit=limit)


@mcp.tool(annotations=WRITE)
def create_thought(
    core_statement: str,
    text: str | None = None,
    categories: list[CategoryPath] | None = None,
    keywords: list[str] | None = None,
    groups: list[str] | None = None,
) -> dict:
    """Legt einen Gedanken an (eigene Überlegung ohne Quelle)."""
    return _call(
        "thoughts.create",
        coreStatement=core_statement,
        text=text,
        categories=categories,
        keywords=keywords,
        groups=groups,
    )


@mcp.tool(annotations=READ)
def list_tasks(reference_id: str | None = None, include_done: bool = False) -> list:
    """Aufgaben eines Titels oder des ganzen Projekts."""
    return _call("tasks.list", referenceId=reference_id, includeDone=include_done)


@mcp.tool(annotations=WRITE)
def create_task(
    name: str,
    reference_id: Annotated[str | None, Field(description="Titel; leer = Projektaufgabe")] = None,
    due_date: Annotated[str | None, Field(description="yyyy-mm-dd")] = None,
    importance: Literal["Low", "Middle", "High"] | None = None,
    state: Literal["NotStarted", "Started", "Advanced", "Done"] | None = None,
    notes: str | None = None,
) -> dict:
    """Legt eine Aufgabe an einem Titel oder im Projekt an."""
    return _call(
        "tasks.create",
        name=name,
        referenceId=reference_id,
        dueDate=due_date,
        importance=importance,
        state=state,
        notes=notes,
    )


@mcp.tool(annotations=WRITE)
def update_task(
    task_id: str,
    name: str | None = None,
    due_date: str | None = None,
    importance: Literal["Low", "Middle", "High"] | None = None,
    state: Literal["NotStarted", "Started", "Advanced", "Done"] | None = None,
    notes: str | None = None,
) -> dict:
    """Ändert eine Aufgabe, z. B. state='Done'."""
    return _call(
        "tasks.update", id=task_id, name=name, dueDate=due_date, importance=importance, state=state, notes=notes
    )


@mcp.tool(annotations=DESTRUCTIVE)
def delete_tasks(task_ids: list[str], confirm: Confirm = False) -> dict:
    """Löscht Aufgaben."""
    return _call("tasks.delete", ids=task_ids, confirm=confirm)


# --- Import, duplicates, deletion -----------------------------------------------------------------


@mcp.tool(annotations=ToolAnnotations(read_only_hint=False, destructive_hint=False, open_world_hint=True))
def import_reference_by_identifier(
    identifier: Annotated[str, Field(description="DOI, ISBN, PubMed-ID oder arXiv-ID")],
    categories: list[CategoryPath] | None = None,
    keywords: list[str] | None = None,
    groups: list[str] | None = None,
    allow_duplicate: bool = False,
) -> dict:
    """Legt einen Titel über Citavis eigene Recherche an (wie 'ISBN, DOI, andere ID' in Citavi).
    Bevorzugt vor import_reference_by_doi; vorhandene DOI/ISBN wird erkannt."""
    return _call(
        "references.importIdentifier",
        timeout=120,
        identifier=identifier,
        categories=categories,
        keywords=keywords,
        groups=groups,
        allowDuplicate=allow_duplicate,
    )


@mcp.tool(annotations=READ)
def find_duplicates() -> dict:
    """Dublettenprüfung über das ganze Projekt: gleiche DOI, ISBN, Titel+Jahr und Citavis eigener Dublettenvergleich."""
    return _call("references.duplicates", timeout=120)


@mcp.tool(annotations=DESTRUCTIVE)
def merge_duplicates(
    keep_id: Annotated[str, Field(description="Titel, der bleibt (meist der mit mehr Wissenselementen/Kategorien)")],
    duplicate_ids: list[str],
    override_data: Annotated[
        bool, Field(description="True: Felder der Dublette überschreiben den behaltenen Titel")
    ] = False,
    confirm: Confirm = False,
) -> dict:
    """Führt Dubletten zusammen: Wissenselemente mit PDF-Markierungen, Anhänge, Aufgaben, Kategorien, Schlagwörter,
    Gruppen und Felder wandern zum behaltenen Titel; die Dublette wird nur gelöscht, wenn alles angekommen ist."""
    return _call(
        "references.merge",
        timeout=180,
        keepId=keep_id,
        duplicateIds=duplicate_ids,
        overrideData=override_data,
        confirm=confirm,
    )


@mcp.tool(annotations=DESTRUCTIVE)
def delete_references(reference_ids: list[str], confirm: Confirm = False) -> dict:
    """Löscht Titel samt Wissenselementen und Anhängen im Projekt."""
    return _call("references.delete", ids=reference_ids, confirm=confirm)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_knowledge_items(
    knowledge_item_ids: list[str], remove_highlights: bool = True, confirm: Confirm = False
) -> dict:
    """Löscht Wissenselemente oder Gedanken, auf Wunsch samt ihrer PDF-Markierung."""
    return _call("knowledgeItems.delete", ids=knowledge_item_ids, removeHighlights=remove_highlights, confirm=confirm)


# --- Merging another project (e.g. a student's shared cloud project) ----------------------------


def _ensure_open(name: str) -> None:
    projects = _projects()
    if any((p.get("name") or "").lower() == name.lower() for p in projects.get("open") or []):
        return
    _call("projects.open", timeout=240, name=name)


@mcp.tool(annotations=READ)
def compare_projects(
    source_project: Annotated[
        str, Field(description="Projekt, aus dem übernommen wird (z. B. das geteilte Hiwi-Projekt)")
    ],
    target_project: Annotated[str, Field(description="Eigenes Projekt, in das übernommen wird")],
) -> dict:
    """Vergleicht zwei Projekte vor dem Zusammenführen: neue Titel, schon vorhandene Titel (DOI/ISBN/Titel+Jahr),
    neue Wissenselemente an vorhandenen Titeln, Kategorien der Quelle und Kategorienpfade des Ziels.
    Danach eine Kategorienzuordnung (Quelle -> eigene Kategorie) vorschlagen und vom Nutzer freigeben lassen."""
    return _compare(source_project, target_project)


def _merge_citation_check(source_project: str, by_id: dict, new_refs: list, matched: list) -> dict:
    """Direct quotations are checked against the source PDF; indirect ones are listed for a manual look."""
    violations, to_review = [], []
    items = [(by_id[r["sourceId"]], by_id[r["sourceId"]].get("knowledgeItems", [])) for r in new_refs]
    items += [
        (
            by_id[m["sourceId"]],
            [
                k
                for k in by_id[m["sourceId"]].get("knowledgeItems", [])
                if k["id"] in {n["id"] for n in m["newKnowledgeItems"]}
            ],
        )
        for m in matched
    ]
    for ref, kis in items:
        pages = None
        for ki in kis:
            kind = ki.get("type")
            if kind == "DirectQuotation" and ki.get("text") and ref.get("attachments"):
                if pages is None:
                    try:
                        pages = _call("pdf.text", timeout=300, _project=source_project, referenceId=ref["id"]).get(
                            "pages", []
                        )
                    except ToolError:
                        pages = []
                verdict = citability.check(pages, ki["text"]) if pages else {"citable": None}
                if verdict.get("citable") is False:
                    violations.append(
                        {
                            "shortTitle": ref.get("shortTitle"),
                            "coreStatement": ki.get("coreStatement"),
                            "violations": verdict["violations"],
                            "citations": verdict.get("citationsInSentence"),
                        }
                    )
            elif kind in ("IndirectQuotation", "Summary"):
                to_review.append(
                    {
                        "shortTitle": ref.get("shortTitle"),
                        "coreStatement": ki.get("coreStatement"),
                        "pageRange": ki.get("pageRange"),
                    }
                )
    return {"directQuotationViolations": violations, "indirectToReview": to_review}


def _compare(source_project: str, target_project: str) -> dict:
    _ensure_open(source_project)
    _ensure_open(target_project)
    source = _call("project.export", timeout=300, _project=source_project)
    target = _call("project.export", timeout=300, _project=target_project)
    return merge.compare(source, target)


@mcp.tool(annotations=WRITE)
def merge_project(
    source_project: str,
    target_project: str,
    category_mapping: Annotated[
        dict[str, str | list[str] | None],
        Field(
            description="Quellkategorie (Pfad) -> Zielkategorie(n) im eigenen Projekt; None/'' = Kategorie bewusst "
            "weglassen. Fehlende Zielkategorien werden angelegt."
        ),
    ],
    reference_ids: Annotated[
        list[str] | None, Field(description="Nur diese Quell-Titel übernehmen; leer = alle aus compare_projects")
    ] = None,
    copy_attachments: bool = True,
    dry_run: Annotated[bool, Field(description="True: nur zeigen, was passieren würde")] = True,
) -> dict:
    """Übernimmt ein anderes Projekt in das eigene: neue Titel komplett (Wissenselemente mit PDF-Markierungen, PDFs,
    Aufgaben, Schlagwörter, Gruppen), bei vorhandenen Titeln nur die neuen Wissenselemente (fehlendes PDF wird
    mitkopiert). Kategorien werden NICHT übernommen, sondern über category_mapping in die eigene Struktur
    einsortiert. Erst mit dry_run=True prüfen, dann mit dry_run=False ausführen."""
    plan = _compare(source_project, target_project)
    wanted = set(reference_ids or [])
    new_refs = [r for r in plan["newReferences"] if not wanted or r["sourceId"] in wanted]
    matched = [
        m for m in plan["matchedReferences"] if (not wanted or m["sourceId"] in wanted) and m["newKnowledgeItems"]
    ]

    source = _call("project.export", timeout=300, _project=source_project)
    by_id = {r["id"]: r for r in source.get("references", [])}
    unmapped: set[str] = set()
    report: dict[str, Any] = {
        "dryRun": dry_run,
        "newReferences": [],
        "knowledgeItemsAdded": [],
        "unmappedCategories": [],
    }

    def target_categories(cats: list[str]) -> list[str]:
        mapped, missing = merge.map_categories(cats, category_mapping)
        unmapped.update(missing)
        return mapped

    for item in new_refs:
        ref = by_id[item["sourceId"]]
        ref_cats = target_categories(ref.get("categories", []))
        ki_cats = {ki["id"]: target_categories(ki.get("categories", [])) for ki in ref.get("knowledgeItems", [])}
        entry = {"shortTitle": ref.get("shortTitle"), "categories": ref_cats, "knowledgeItems": len(ki_cats)}
        if not dry_run:
            copied = _call(
                "references.copyToProject",
                timeout=300,
                _project=source_project,
                referenceIds=[ref["id"]],
                targetProject=target_project,
                copyAttachments=copy_attachments,
            )["copied"][0]
            entry["targetId"] = copied["targetId"]
            if ref_cats:
                _call("categories.set", _project=target_project, categories=ref_cats, referenceIds=[copied["targetId"]])
            for pair in copied["knowledgeItems"]:
                cats = ki_cats.get(pair["sourceId"])
                if cats and pair.get("targetId"):
                    _call(
                        "categories.set", _project=target_project, categories=cats, knowledgeItemIds=[pair["targetId"]]
                    )
        report["newReferences"].append(entry)

    for m in matched:
        ki_ids = [ki["id"] for ki in m["newKnowledgeItems"]]
        ki_cats = {ki["id"]: target_categories(ki.get("categories", [])) for ki in m["newKnowledgeItems"]}
        entry = {"shortTitle": m["shortTitle"], "matchedBy": m["matchedBy"], "knowledgeItems": len(ki_ids)}
        if not dry_run:
            copied = _call(
                "knowledgeItems.copyTo",
                timeout=300,
                _project=source_project,
                knowledgeItemIds=ki_ids,
                targetReferenceId=m["targetId"],
                targetProject=target_project,
                copyAttachments=copy_attachments and not m["targetHasAttachment"],
            )
            for pair in copied["knowledgeItems"]:
                cats = ki_cats.get(pair["sourceId"])
                if cats and pair.get("targetId"):
                    _call(
                        "categories.set", _project=target_project, categories=cats, knowledgeItemIds=[pair["targetId"]]
                    )
            entry["report"] = copied["report"]
        report["knowledgeItemsAdded"].append(entry)

    report["citationCheck"] = _merge_citation_check(source_project, by_id, new_refs, matched)
    report["unmappedCategories"] = sorted(unmapped)
    if unmapped:
        report["hint"] = (
            "Für diese Quellkategorien gab es keine Zuordnung; die Elemente sind ohne sie übernommen. "
            "Mit list_uncategorized prüfen und mit set_categories nachsortieren."
        )
    return report


# --- Reading sources and citation rules ----------------------------------------------------------


@mcp.tool(annotations=READ)
def get_pdf_text(
    reference_id: str,
    from_page: Annotated[int, Field(ge=1, description="PDF-Seite")] = 1,
    to_page: Annotated[int, Field(ge=0, description="PDF-Seite, 0 = bis zum Ende")] = 0,
) -> dict:
    """Text des PDFs eines Titels, Seite für Seite (PDF-Seite, gedruckte Seitenzahl falls im PDF hinterlegt).
    Zum einmaligen vollständigen Lesen einer Quelle (Arbeitsablauf in docs/ZITIERREGELN.md)."""
    return _call("pdf.text", timeout=300, referenceId=reference_id, fromPage=from_page, toPage=to_page)


@mcp.tool(annotations=READ)
def check_citability(
    reference_id: str,
    passage: Annotated[str, Field(description="Exakter Wortlaut der Stelle im PDF")],
    pdf_page: int | None = None,
) -> dict:
    """Prüft eine Stelle gegen die Zitierregeln: Haupttext statt Abstract/Vorspann/Literaturverzeichnis und
    eigene Aussage statt mit fremder Quelle belegtem Satz. Liefert Satz, gefundene Belege (Spur zur Primärquelle)
    und die gedruckte Seitenzahl."""
    return _check_citability(reference_id, passage, pdf_page)


# --- RIS / BibTeX ---------------------------------------------------------------------------------


@mcp.tool(annotations=WRITE)
def import_file(
    path: Annotated[str, Field(description="Pfad zu einer .ris- oder .bib-Datei")],
    categories: list[CategoryPath] | None = None,
    keywords: list[str] | None = None,
    groups: list[str] | None = None,
    skip_duplicates: Annotated[bool, Field(description="Titel mit gleicher DOI/ISBN/Titel+Jahr überspringen")] = True,
    dry_run: bool = False,
) -> dict:
    """Importiert RIS oder BibTeX in das Projekt (Autoren, Herausgeber, Zeitschrift, Verlag, DOI, ISBN, Seiten,
    Abstract, Schlagwörter). Dubletten werden übersprungen; mit dry_run nur anzeigen."""
    from pathlib import Path

    raw = Path(path).read_bytes()
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    types = {t.get("key") for t in _call("references.types") or [] if t.get("key")} or None
    is_bib = path.lower().endswith(".bib") or text.lstrip().startswith("@")
    records = bibio.parse_bibtex(text, types) if is_bib else bibio.parse_ris(text, types)

    existing = {}
    if skip_duplicates:
        export = _call("project.export", timeout=300, includeKnowledgeItems=False)
        for ref in export.get("references", []):
            for key in merge.reference_keys(ref):
                existing[key] = ref
    added, skipped = [], []
    for rec in records:
        probe = {
            "title": rec.get("fields", {}).get("Title"),
            "year": rec.get("fields", {}).get("Year"),
            "doi": rec.get("fields", {}).get("Doi"),
            "isbn": rec.get("fields", {}).get("Isbn"),
        }
        dup = next((existing[k] for k in merge.reference_keys(probe) if k in existing), None)
        if dup:
            skipped.append({"title": probe["title"], "existing": dup.get("shortTitle"), "existingId": dup.get("id")})
            continue
        if dry_run:
            added.append({"title": probe["title"], "type": rec.get("type")})
            continue
        params = dict(rec)
        for name, value in (("categories", categories), ("groups", groups)):
            if value:
                params[name] = value
        if keywords:
            params["keywords"] = list(dict.fromkeys((params.get("keywords") or []) + keywords))
        created = _call("references.create", **params)
        ref = created.get("reference", {})
        added.append({"id": ref.get("id"), "shortTitle": ref.get("shortTitle"), "warnings": created.get("warnings")})
        for k in merge.reference_keys(probe):
            existing[k] = ref
    return {"format": "BibTeX" if is_bib else "RIS", "records": len(records), "added": added, "skipped": skipped}


@mcp.tool(annotations=READ)
def export_file(
    path: str,
    file_format: Literal["ris", "bibtex"] = "ris",
    reference_ids: list[str] | None = None,
    category_path: CategoryPath | None = None,
) -> dict:
    """Exportiert Titel als RIS (Citavis Exportfilter) oder BibTeX."""
    if file_format == "ris":
        return _call(
            "references.exportFile",
            timeout=300,
            path=path,
            format="ris",
            referenceIds=reference_ids or [],
            categoryPath=category_path,
        )
    from pathlib import Path

    export = _call("project.export", timeout=300, referenceIds=reference_ids or [], includeKnowledgeItems=False)
    refs = export.get("references", [])
    if category_path:
        refs = [
            r
            for r in refs
            if any(c == category_path or c.startswith(category_path + " > ") for c in r.get("categories", []))
        ]
    Path(path).write_text(bibio.to_bibtex(refs), encoding="utf-8")
    return {"path": path, "format": "BibTeX", "references": len(refs)}


# --- Cross references, knowledge organizer, saved searches, lists, projects -----------------------


@mcp.tool(annotations=READ)
def list_reference_links(reference_id: str) -> list:
    """Querverweise eines Titels (verweist auf / wird verwiesen von)."""
    return _call("links.list", referenceId=reference_id)


@mcp.tool(annotations=WRITE)
def link_references(
    source_id: Annotated[str, Field(description="Titel, der verweist")],
    target_id: Annotated[str, Field(description="Titel, auf den verwiesen wird")],
    relation: Literal["Neutral", "SomewhatPositive", "Positive", "SomewhatNegative", "Negative"] | None = None,
    notes: str | None = None,
    pages: Annotated[str | None, Field(description="Fundstelle im verweisenden Titel")] = None,
) -> dict:
    """Legt einen Querverweis an, z. B. wenn eine Quelle eine andere zitiert (Spur zur Primärquelle)."""
    return _call("links.create", sourceId=source_id, targetId=target_id, relation=relation, notes=notes, pages=pages)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_reference_links(link_ids: list[str], confirm: Confirm = False) -> dict:
    """Löscht Querverweise."""
    return _call("links.delete", linkIds=link_ids, confirm=confirm)


@mcp.tool(annotations=READ)
def list_organizer(path: CategoryPath) -> list:
    """Wissenselemente einer Kategorie in der Reihenfolge des Wissensorganisators (mit Zwischenüberschriften)."""
    return _call("organizer.list", path=path)


@mcp.tool(annotations=WRITE)
def reorder_knowledge_items(path: CategoryPath, order: list[str]) -> list:
    """Legt die Reihenfolge der Wissenselemente in einer Kategorie fest (Argumentationsfolge fürs Schreiben)."""
    return _call("organizer.reorder", path=path, order=order)


@mcp.tool(annotations=WRITE)
def add_subheading(path: CategoryPath, text: str, after_knowledge_item_id: str | None = None) -> dict:
    """Fügt im Wissensorganisator eine Zwischenüberschrift in eine Kategorie ein."""
    return _call("organizer.addSubheading", path=path, text=text, afterKnowledgeItemId=after_knowledge_item_id)


@mcp.tool(annotations=READ)
def list_saved_searches() -> list:
    """Gespeicherte Suchen des Projekts."""
    return _call("savedSearches.list")


@mcp.tool(annotations=WRITE)
def save_search(name: str, query: str, match_case: bool = False, match_whole_word: bool = False) -> dict:
    """Speichert eine Suche (Citavi-Suchsyntax) für die Oberfläche."""
    return _call("savedSearches.create", name=name, query=query, matchCase=match_case, matchWholeWord=match_whole_word)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_saved_searches(names: list[str], confirm: Confirm = False) -> dict:
    """Löscht gespeicherte Suchen."""
    return _call("savedSearches.delete", names=names, confirm=confirm)


ListKind = Literal["persons", "periodicals", "publishers", "seriesTitles"]


@mcp.tool(annotations=READ)
def list_entries(kind: ListKind, query: str | None = None, only_unused: bool = False, limit: int = 500) -> list:
    """Personen, Zeitschriften, Verlage oder Reihen mit Anzahl der Titel (zum Pflegen und Finden von Doppelungen)."""
    return _call("lists.get", kind=kind, query=query, onlyUnused=only_unused, limit=limit)


@mcp.tool(annotations=WRITE)
def update_entry(kind: ListKind, entry_id: str, fields: dict[str, str | None]) -> dict:
    """Korrigiert einen Listeneintrag, z. B. {"FirstName": "Günther"} oder {"Issn": "0020-7543"}."""
    return _call("lists.update", kind=kind, id=entry_id, fields=fields)


@mcp.tool(annotations=WRITE)
def merge_entries(kind: ListKind, target_id: str, source_ids: list[str]) -> dict:
    """Führt doppelte Personen, Zeitschriften, Verlage oder Reihen zusammen (alle Titel zeigen danach auf das Ziel)."""
    return _call("lists.merge", kind=kind, targetId=target_id, sourceIds=source_ids)


@mcp.tool(annotations=DESTRUCTIVE)
def delete_unused_entries(kind: ListKind, entry_ids: list[str] | None = None, confirm: Confirm = False) -> dict:
    """Löscht Listeneinträge, die kein Titel verwendet."""
    return _call("lists.deleteUnused", kind=kind, ids=entry_ids or [], confirm=confirm)


@mcp.tool(annotations=WRITE)
def create_project(name: str, project_type: Literal["cloud", "local"] = "cloud") -> dict:
    """Legt ein neues Citavi-Projekt an (Standard: Cloud-Projekt) und öffnet es. Nur nach Rückfrage beim Nutzer."""
    result = _call("projects.create", timeout=240, name=name, type=project_type)
    bridge.project = name
    return {**(result or {}), "selectedProject": name}


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
