# Changelog

Alle nennenswerten Änderungen an diesem Projekt. Format nach
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach
[Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Changed
- Makro-Anleitung: Der Makro-Editor darf nach dem Start geschlossen werden. Die Verbindung läuft bis zum Beenden von
  Citavi weiter (live geprüft, #7).

### Fixed
- Lokale Projekte (SQLite) lassen sich anlegen: `projects.create` mit `type=local` setzt jetzt die Projektdatei in Citavis Projektordner (vorher `ArgumentNullException: options.ConnectionString`). Optional `folder`. Lokale Projekte live geprüft: Titel, PDF-Anhang, Kategorien, PDF-Text, Wissenselement mit Markierung, Zitierprüfung, Neustart (#5).

## [0.4.1] - 2026-11-09

### Changed
- Installationswege neutral beschrieben: Weg A Add-on auf dem eigenen Rechner, Weg B Add-on auf dem Dienstrechner (Installation
  durch die IT), Weg C Citavi-Makro auf dem Dienstrechner. Für Dienstrechner wird Weg B empfohlen, die IT-Anleitung beschreibt Weg C offen.
- Makro: Hinweis bei fehlender DLL verweist auf Einrichten aus dem Makro-Paket. Neuer Screenshot des Makro-Editors.
- Einrichtung für Nutzer über `einrichten.py` (Doppelklick auf `Einrichten.cmd` bzw. `Claude-verbinden.cmd`) statt PowerShell-Skripten.
  `makro-einrichten.ps1` und `nutzer-einrichten.ps1` entfallen.

## [0.4.0] - 2026-11-09

Erste öffentliche Fassung. Die Entwicklungsgeschichte bis hierher steht in den Einträgen unten.

### Added (öffentliches Repository)
- MIT-Lizenz, `CONTRIBUTING.md`, `SECURITY.md`, `CODEOWNERS`. Hinweis, dass das Projekt weder mit Lumivero noch mit
  Anthropic verbunden ist.

### Added (v0.4, auf Citavi 6.20 geprüft)
- Drei Installationswege mit fertigen Paketen: Add-on auf dem eigenen Rechner, Add-on auf dem Dienstrechner über die IT
  (Anleitung als PDF), Citavi-Makro auf dem Dienstrechner (bebilderte Anleitung als PDF).
  Offline-Installation, SHA256 für DLL und alle Python-Pakete, Quelltext liegt bei.
- Zitierregeln als verbindliche Datei [docs/ZITIERREGELN.md](docs/ZITIERREGELN.md), vom MCP-Server in seine Anweisungen
  geladen: nur Haupttext, kein Abstract, keine mit fremder Quelle belegten Sätze, Primärquelle suchen, gedruckte
  Seitenzahl. Arbeitsablauf zum einmaligen vollständigen Auswerten einer Quelle mit Index.
- `check_citability` und automatische Prüfung in `create_knowledge_item` (lehnt Abstract, Vorspann,
  Literaturverzeichnis und Sekundärzitate ab, übernimmt die gedruckte Seitenzahl). Prüfung auch beim
  Übernehmen fremder Projekte.
- PDF-Text seitenweise (`pdf.text`, `get_pdf_text`).
- Cloud-Projekte ohne Klick anlegen (`projects.create`, `create_project`).
- RIS- und BibTeX-Import mit Dublettenprüfung, RIS-Export über Citavi, BibTeX-Export.
- Querverweise zwischen Titeln, Reihenfolge und Zwischenüberschriften im Wissensorganisator, gespeicherte Suchen,
  Personen, Zeitschriften, Verlage und Reihen pflegen und zusammenführen.
- Projektübernahme live geprüft: zwei Cloud-Projekte zusammengeführt (neue Titel komplett, bei vorhandenen nur
  neue Wissenselemente, eigene Kategorien, keine Dubletten, zweiter Lauf ohne Änderungen).

### Fixed
- Wissenselemente einer Kategorie werden über ihre eigenen Kategorien zugeordnet (über die Liste der Kategorie
  ging die Zuordnung beim Neustart verloren); Reihenfolge übersteht den Neustart.

### Added (v0.3, auf Citavi 6.20 geprüft)
- Kategorienbaum umbauen: umbenennen, verschieben (andere Oberkategorie, Position), Reihenfolge, alphabetisch
  sortieren, zusammenführen, löschen (optional mit Überführung), Inhalte anzeigen, Kategorien ersetzen
  (neu einsortieren), Elemente ohne Kategorie finden.
- Gruppen (anlegen, zuweisen, umbenennen, zusammenführen, löschen) für Titel und Wissenselemente; Schlagwörter
  umbenennen, zusammenführen, löschen, Nutzung zählen.
- Gedanken (Wissenselemente ohne Titel), Aufgaben an Titeln und im Projekt, Relevanz an Wissenselementen,
  Sterne, rote/blaue Marke und Freitextfelder 1 bis 9 an Titeln.
- Import über Citavis eigene Recherche: DOI, ISBN, PubMed-ID, arXiv (`references.importIdentifier`).
- Dublettenprüfung über das ganze Projekt (DOI, ISBN, Titel+Jahr, Citavis `ReferenceDuplicateComparer`) und
  Zusammenführen mit Übernahme von Wissenselementen samt PDF-Markierungen, Anhängen und Aufgaben.
- Löschen von Titeln, Wissenselementen, Aufgaben, Kategorien, Schlagwörtern und Gruppen nur mit `confirm=true` (#4).
- Projekt übernehmen (z. B. geteiltes Hiwi-Projekt): `compare_projects`, `merge_project` mit Kategorienzuordnung
  auf die eigene Struktur, Probelauf (`dry_run`), Kopie über Citavis Klon-Logik (`CloneWithResults`).

### Fixed
- `MergeReference` übernimmt in Citavi 6.20 keine Wissenselemente, Anhänge und Aufgaben; die werden jetzt vorher
  kopiert, die Dublette wird nur gelöscht, wenn alles angekommen ist.
- Textvergleich beim Zusammenführen behandelt Umlaute und ihre Umschreibung (ö/oe) gleich.

### Added (v0.2, erster Betrieb auf echtem Citavi 6.20, Heimserver)
- Add-on läuft nativ aus `Citavi 6\AddOns` und startet die Bridge **schon beim Laden von Citavi**, auch wenn nur
  das Startfenster offen ist. Kein Makro und kein Klick mehr nötig.
- Projekte ohne Klick öffnen: Bridge `projects.open`, MCP `open_project`. `projects.list` liefert zusätzlich Citavis
  Projektliste (`known`). `select_project` öffnet ein bekanntes, noch geschlossenes Projekt automatisch.
- `bridge.json`: `autoOpenProjects` öffnet Projekte beim Citavi-Start.
- MCP-Server startet Citavi selbst, wenn es nicht läuft (abschaltbar mit `CITAVI_AUTOSTART=0`).
- `scripts/deploy.ps1`: bauen, Citavi beenden, Add-on kopieren, Citavi neu starten (für Rechner ohne Bildschirmzugriff).

### Fixed
- Feldnamen sind nicht mehr groß-/kleinschreibungsabhängig (`title` statt nur `Title`), auf echtem Citavi gefunden.
- Windows: geschlossener Port meldet `ConnectTimeout`; wird jetzt als „Citavi läuft nicht“ erkannt.
- Installationsskript bricht mit klarer Meldung ab, wenn Claude den MCP-Server noch geöffnet hat (gesperrte `citavi-mcp.exe`).
- Add-on-Basisklasse ist in Citavi 6 `CitaviAddOn<MainForm>` (nicht `CitaviAddOnEx`), ermittelt auf echtem Citavi 6.
- Verweis auf `SwissAcademic.Controls.dll` ergänzt (Basisklasse des Citavi-Hauptfensters).
- Anhänge: `attachments.add` nutzt Citavis `LinkedResource` mit `AttachmentAction`, damit Citavi die Datei
  selbst kopiert bzw. in die Cloud hochlädt (vorher `api_mismatch`).
- Cloud-Download: Cloud-Anhänge (`AttachmentRemote`) werden gezielt über `LinkedResource.CacheAsync` geladen
  und aus `CacheFilePath` kopiert.
- `bridge.json`: `citationStyleFolders` für eigene Zitationsstil-Ordner (z. B. verlegter Citavi-Ordner).
- Zitationsstil über `ProjectUserSettings.CitationStyle` / `ChangeCitationStyleAsync` und die Liste
  `CitationStyleManager.KnownCitationStyles` (auf Citavi 6 ermittelt). `set_citation_style` nimmt jetzt
  Name oder id statt Dateipfad.
- Cloud-Anhänge zeigen den Originaldateinamen statt des GUID-Namens im Zwischenspeicher.
- Interne Platzhalter (`KnowledgeItem_Mirroring:…`) werden nicht mehr als Feldwerte ausgegeben.

### Added
- PDF-Markierungen (#2): Text im PDF suchen (PDFTron aus Citavi), Markierung (`Annotation` mit `Quads`) anlegen und
  per `EntityLink` (`PdfKnowledgeItemIndication`) mit dem Wissenselement verknüpfen. Bridge `pdf.findText`,
  `pdf.highlight`; MCP `create_knowledge_item(highlight_in_pdf=True)`, `highlight_in_pdf`, `find_text_in_pdf`.
- Diagnose `debug.inspect` / `inspect_citavi_object`: interner Aufbau von Wissenselementen, Verknüpfungen und PDF-Markierungen (Vorbereitung #2).
- Start der Bridge per Citavi-Makro (`bridge/macro/StartClaudeBridge.cs`, `install.ps1 -Macro`) für Rechner, auf
  denen das Add-on nicht im AddOns-Ordner installiert ist. Citavi 6 lädt Add-ons nur aus diesem Ordner.

### Added
- Citavi-Add-on `ClaudeCitavi.Bridge`: lokaler HTTP-Server (nur localhost, Token) im Citavi-Prozess
  mit Methoden für Titel, Kategorien, Schlagwörter, Wissenselemente, Anhänge und Zitationsstil.
- Diagnosemethode `debug.describeType` zum Nachschlagen der Citavi-API vor Ort.
- MCP-Server `citavi-mcp` mit 25 Werkzeugen für Claude, inklusive DOI-Import über Crossref.
- Installationsskript `scripts/install.ps1` für Windows (Add-on, MCP-Server, Claude-Desktop-Eintrag).
- Entwicklungsumgebung auch ohne `winget`: .NET SDK per `dotnet-install.ps1` im Benutzerprofil.
- Dokumentation: Architektur, ADR 0001, Installation, Bridge-Protokoll, Entwicklung, GitHub-Leitfaden.
- GitHub: Issue- und PR-Vorlagen, CI (Python-Tests, Compile-Check des Add-ons), Dependabot.
- Cloud-Projekte und mehrere offene Projekte: `projects.list`, Projektauswahl pro Anfrage,
  MCP-Werkzeuge `list_open_projects` und `select_project`.
- Anhänge auflisten mit Speicherort (lokal/Cloud) und herunterladen: `attachments.list`,
  `attachments.download`, MCP-Werkzeuge `list_attachments` und `download_attachments`. Cloud-PDFs,
  die von anderen Personen oder Rechnern hochgeladen wurden, lädt Citavi mit dem eigenen Login.
- Diagnose `debug.findMembers` / `find_citavi_members`.
- Bedienungsanleitung `docs/NUTZUNG.md`.
