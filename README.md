# ClaudeCitavi

Claude arbeitet direkt in deinem laufenden **Citavi**: Literatur anlegen, Wissenselemente erfassen,
Kategorien und Schlagwörter vergeben, PDFs anhängen, Zitationsstil setzen. Alles im Hintergrund,
während Citavi geöffnet ist, und sofort in der Citavi-Oberfläche sichtbar.

> **Status:** v0.4, im Einsatz mit Citavi 6.20 und Cloud-Projekten. Auf echtem Citavi geprüft: Projekte öffnen und
> anlegen ohne Klick, Titel per DOI, ISBN, RIS und BibTeX, PDFs hochladen und lesen, Wissenselemente mit Markierung im
> PDF, Kategorienbaum umbauen, Dubletten zusammenführen, zwei Cloud-Projekte zusammenführen. Nicht getestet: lokale
> Projekte, Citavi DBServer, Citavi 7. Offene Punkte in der [Issue-Liste](../../issues).

## So funktioniert es

```
 Claude (Desktop / Code)
        │  MCP (stdio)
        ▼
 citavi-mcp            Python, läuft lokal auf deinem Rechner
        │  HTTP, nur 127.0.0.1, mit Token
        ▼
 ClaudeCitavi.Bridge   C#-Add-on, läuft innerhalb von Citavi
        │  Citavi-Objektmodell (wie ein Klick in der Oberfläche)
        ▼
 Dein Citavi-Projekt
```

Warum so? Siehe [docs/ARCHITEKTUR.md](docs/ARCHITEKTUR.md) und die Entscheidung
[ADR 0001](docs/adr/0001-addon-bridge-und-mcp.md).

## Was Claude damit kann

| Bereich | Werkzeuge (MCP-Tools) |
|---|---|
| Verbindung & Projekte | `citavi_status`, `list_open_projects`, `open_project`, `select_project`, `create_project` (lokale und Cloud-Projekte, öffnen und anlegen ohne Klick) |
| Literatur | `search_references`, `get_reference`, `create_reference`, `update_reference`, `import_reference_by_identifier` (DOI, ISBN, PMID, arXiv über Citavi), `import_reference_by_doi` (Crossref), `list_reference_types`, `delete_references` |
| Dubletten | `find_duplicates` (DOI, ISBN, Titel+Jahr, Citavis Dublettenvergleich), `merge_duplicates` (Wissenselemente, Markierungen, PDFs, Aufgaben wandern mit) |
| Kategorienbaum | `list_categories`, `create_category`, `rename_category`, `move_category`, `reorder_categories`, `sort_categories`, `merge_categories`, `delete_category`, `list_category_contents`, `assign_categories`, `set_categories`, `list_uncategorized` |
| Schlagwörter & Gruppen | `list_keywords`, `keyword_usage`, `assign_keywords`, `list_groups`, `assign_groups`, `rename_keyword_or_group`, `merge_keywords_or_groups`, `delete_keywords_or_groups` |
| Wissenselemente & Gedanken | `list_knowledge_items`, `create_knowledge_item` (auf Wunsch mit Markierung im PDF), `update_knowledge_item`, `delete_knowledge_items`, `list_thoughts`, `create_thought` |
| Aufgaben | `list_tasks`, `create_task`, `update_task`, `delete_tasks` |
| Projekt übernehmen | `compare_projects`, `merge_project` (z. B. geteiltes Hiwi-Projekt; eigene Kategorien bleiben, Zuordnung per Mapping) |
| Quellen auswerten | `get_pdf_text`, `check_citability` (Zitierregeln aus [docs/ZITIERREGELN.md](docs/ZITIERREGELN.md), in `create_knowledge_item` automatisch) |
| RIS und BibTeX | `import_file` (mit Dublettenprüfung), `export_file` |
| Querverweise | `list_reference_links`, `link_references`, `delete_reference_links` |
| Wissensorganisator | `list_organizer`, `reorder_knowledge_items`, `add_subheading` |
| Gespeicherte Suchen | `list_saved_searches`, `save_search`, `delete_saved_searches` |
| Listen pflegen | `list_entries`, `update_entry`, `merge_entries`, `delete_unused_entries` (Personen, Zeitschriften, Verlage, Reihen) |
| PDF-Markierungen | `highlight_in_pdf`, `find_text_in_pdf` |
| PDFs / Dateien | `attach_file`, `list_attachments`, `download_attachments` (auch Cloud-PDFs anderer Personen/Rechner lokal ablegen) |
| Zitationsstil | `get_citation_style`, `list_citation_styles`, `set_citation_style` (experimentell) |
| Diagnose | `describe_citavi_type`, `find_citavi_members` |

Löschen und Zusammenführen verlangen `confirm=True` und laufen nur nach Zustimmung.

## Welcher Weg passt zu dir?

Es gibt drei Wege, ClaudeCitavi zu installieren. Alle drei bieten dieselben Funktionen. Die fertigen Pakete liegen unter
[Releases](../../releases), Quelltext ist zum Installieren nicht nötig.

| | Weg A: Add-on auf dem eigenen Rechner | Weg B: Add-on auf dem Dienstrechner | Weg C: Citavi-Makro auf dem Dienstrechner |
|---|---|---|---|
| **Wer wird gebraucht** | niemand, du installierst selbst mit deinen Adminrechten | die IT deiner Einrichtung für die einmalige Installation des Add-ons | niemand, du richtest es selbst ein |
| **Start der Verbindung** | automatisch mit Citavi | automatisch mit Citavi | bei jeder Citavi-Sitzung neu über das Makro (Alt+F11, Datei öffnen, F5), etwa 1 Minute |
| **Paket** | `ClaudeCitavi-<Version>-AddOn.zip` | `ClaudeCitavi-<Version>-AddOn.zip` | `ClaudeCitavi-<Version>-Makro.zip` |
| **Anleitung** | [Weg A](docs/INSTALLATION.md#weg-a-add-on-auf-dem-eigenen-rechner) | [Anleitung für die IT](docs/IT-INSTALLATION.md) (als PDF im Paket) | [Bebilderte Anleitung](docs/anleitung-makro/ANLEITUNG-MAKRO.md) (als PDF im Paket) |

**Wichtig bei Weg C:** Die Verbindung läuft nur, solange Citavi offen ist. Nach jedem Neustart von Citavi muss das Makro
wieder gestartet werden. Bei Weg A und B startet sie von selbst.

Weg C nutzt die Makro-Funktion, die Citavi jedem Nutzer anbietet, und installiert nichts in Programmordnern. Er eignet
sich zum Ausprobieren und für Rechner, auf denen das Add-on noch nicht installiert ist. Für den dauerhaften Einsatz auf
Dienstrechnern ist Weg B gedacht, in Absprache mit der IT. Bitte vor der Nutzung auf einem Dienstrechner klären, ob
Citavi-Makros und Claude nach den Regeln deiner Einrichtung genutzt werden dürfen.

Für alle Wege nötig: Windows, Citavi 6 Desktop, Python 3.12 und Claude Desktop oder Claude Code. Danach Citavi starten
und in Claude fragen: *„Prüfe die Verbindung zu Citavi.“*

**Bedienung mit Beispielen: [docs/NUTZUNG.md](docs/NUTZUNG.md).** Verbindliche Regeln für Zitate:
[docs/ZITIERREGELN.md](docs/ZITIERREGELN.md).

Entwickler bauen aus dem Quelltext mit `scripts\install.ps1` (braucht .NET SDK 8) und Pakete mit
`scripts\build-release.ps1`, siehe [docs/ENTWICKLUNG.md](docs/ENTWICKLUNG.md).

## Repository-Aufbau

| Ordner | Inhalt |
|---|---|
| `bridge/` | Citavi-Add-on (C#, .NET Framework 4.8) |
| `mcp-server/` | MCP-Server für Claude (Python) |
| `scripts/` | Installationsskript für Windows |
| `docs/` | Nutzung, Architektur, Installation, Protokoll, GitHub-Leitfaden, Entscheidungen (ADR) |
| `.github/` | Vorlagen für Issues und Pull Requests, automatische Tests (CI) |

## Roadmap

Die Planung liegt als [Issues](../../issues) auf GitHub. Grobe Reihenfolge:

1. **v0.1 Inbetriebnahme:** Add-on auf echtem Citavi testen, API-Abweichungen korrigieren.
2. **v0.2 Wissenselemente im PDF:** Text im PDF finden und als Markierung mit Wissenselement verknüpfen.
3. **v0.3 Komfort:** Gedanken, Gruppen, Aufgaben, Import per ISBN, Löschen mit Rückfrage.
4. **Später:** Lesezugriff ohne laufendes Citavi (direkt aus der Projektdatei).

## Mitwirken

Beiträge sind willkommen. Fehler und Ideen als [Issue](../../issues), Änderungen als Pull Request aus einem Fork.
Der Ablauf steht in [CONTRIBUTING.md](CONTRIBUTING.md), Sicherheitslücken bitte vertraulich nach
[SECURITY.md](SECURITY.md) melden. Gemergt wird nur, was der Maintainer geprüft hat. Wie das Projekt GitHub nutzt,
steht in [docs/GITHUB-LEITFADEN.md](docs/GITHUB-LEITFADEN.md), Änderungen je Version in [CHANGELOG.md](CHANGELOG.md).

## Lizenz und Hinweis

[MIT-Lizenz](LICENSE). Nutzung auf eigene Verantwortung, vor dem ersten produktiven Einsatz eine **Sicherung des
Citavi-Projekts** anlegen.

Citavi ist ein Produkt von Lumivero, Claude ein Produkt von Anthropic. Dieses Projekt ist unabhängig und weder mit
Lumivero noch mit Anthropic verbunden. Das Add-on nutzt das interne Objektmodell von Citavi, das nicht offiziell
dokumentiert ist.
