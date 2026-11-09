# Entwicklung

## Lokale Entwicklung

### MCP-Server (Python, läuft auf jedem Betriebssystem)

```bash
cd mcp-server
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
pytest            # Tests gegen eine simulierte Bridge (tests/conftest.py)
ruff check . && ruff format --check .
```

Neue Bridge-Methode? In `tests/conftest.py` (`FakeBridge`) nachbilden und testen.

### Bridge-Add-on (C#)

Bauen auf Windows mit installiertem Citavi:

```powershell
dotnet build bridge\src\ClaudeCitavi.Bridge -c Release -p:CitaviDir="C:\Program Files (x86)\Citavi 6\bin"
```

Ohne Citavi (z. B. in CI oder Linux) gegen Platzhalter-Assemblies, nur zur Syntaxprüfung:

```bash
bridge/ci-stubs/build-stubs.sh /tmp/citavi-stubs
dotnet build bridge/src/ClaudeCitavi.Bridge -c Release -p:CitaviDir=/tmp/citavi-stubs
```

### Aufbau des Add-ons

| Datei | Aufgabe |
|---|---|
| `Addon.cs` | Einstiegspunkt, startet den Server einmal pro Citavi-Prozess |
| `BridgeServer.cs` | HTTP, Token-Prüfung, Fehlerformat |
| `BridgeConfig.cs` | `bridge.json`, Log-Datei |
| `RpcDispatcher.cs` | Methodenregister, UI-Thread, Nachschlagehilfen (Titel, Kategorien, Personen …) |
| `Cv.cs` | Reflection-Hilfen für das Citavi-Objektmodell |
| `Serializer.cs` | Citavi-Objekte → JSON |
| `Handlers/*.cs` | Eine Datei je Bereich; neue Methode = `d.Read(...)` oder `d.Write(...)` |

## Citavi-API nachschlagen

Citavis Objektmodell ist nicht offiziell dokumentiert. Vorgehen, wenn ein Feld oder eine Methode
nicht passt:

1. In Claude: *„Beschreibe den Citavi-Typ Reference"* (Werkzeug `describe_citavi_type`) oder direkt
   `debug.describeType` per PowerShell (siehe [INSTALLATION.md](INSTALLATION.md#funktioniert-es)).
2. Ausgabe in ein Issue kopieren. Daraus wird die Korrektur abgeleitet.

## Checkliste Inbetriebnahme (Issue „v0.1 Inbetriebnahme")

Mit einem **Testprojekt** (nicht dem Dissertationsprojekt) durchgehen:

- [ ] Add-on baut gegen echtes Citavi (`dotnet build`)
- [ ] Citavi startet ohne Fehlermeldung, `bridge.json` und `bridge.log` existieren
- [ ] `citavi_status` liefert Projektname
- [ ] `list_reference_types` liefert Dokumenttypen
- [ ] `create_reference` (Buch mit Autor, Verlag, Ort, Jahr, Kategorie) → erscheint sofort in Citavi, keine `warnings`
- [ ] `import_reference_by_doi` mit `10.1038/nature14539` → Zeitschriftenaufsatz mit Zeitschrift „Nature"
- [ ] `search_references` findet den Titel
- [ ] `create_category` „Test > Unterkategorie" → Hierarchie korrekt
- [ ] `assign_categories` / `assign_keywords` auf Titel
- [ ] `create_knowledge_item` (direct, mit Seite und Kategorie) → im Wissensorganisator sichtbar
- [ ] `attach_file` mit `copy` → PDF im Anhangordner und als Standort sichtbar, lässt sich öffnen
- [ ] `get_citation_style`, `list_citation_styles`, `set_citation_style`
- [ ] Citavi schließen und neu öffnen → alle Änderungen gespeichert

Zusätzlich mit einem **Cloud-Projekt** (Issue „Cloud-Projekte"):

- [ ] Lokales und Cloud-Projekt gleichzeitig offen → `list_open_projects` zeigt beide, `isCloud` korrekt
- [ ] `select_project` auf das Cloud-Projekt → `create_reference` erscheint nach Sync auf einem zweiten Rechner
- [ ] PDF auf Rechner B hochladen → auf Rechner A zeigt `list_attachments(only_missing=True)` die Datei als `cloud`
- [ ] `download_attachments` → Status `downloaded`, Datei liegt im Zielordner und öffnet sich
- [ ] Falls `not_available`: `find_citavi_members` mit Muster `Download` ausführen, Ausgabe ins Issue
- [ ] Mit `readOnly: true` werden Schreibbefehle abgelehnt
