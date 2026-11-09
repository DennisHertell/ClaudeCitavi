# Bridge-Protokoll

Schnittstelle zwischen MCP-Server und Citavi-Add-on. Technisch, für Entwicklung und Fehlersuche.

## Transport

```
POST http://127.0.0.1:<port>/rpc
Authorization: Bearer <token>
Content-Type: application/json

{"method": "<name>", "params": { ... }, "project": "<optional: Name eines offenen Projekts>"}
```

Ohne `project` arbeitet die Bridge mit dem zuletzt aktiven Citavi-Fenster.

Antwort:

```json
{"ok": true,  "result": { ... }}
{"ok": false, "error": {"code": "not_found", "message": "Reference ... not found."}}
```

`port` und `token` stehen in `%APPDATA%\ClaudeCitavi\bridge.json`. Überschreibbar für den MCP-Server
über die Umgebungsvariablen `CITAVI_BRIDGE_URL`, `CITAVI_BRIDGE_TOKEN`, `CITAVI_BRIDGE_CONFIG`.

## Fehlercodes

| Code | HTTP | Bedeutung |
|---|---|---|
| `unauthorized` | 401 | Token fehlt oder falsch |
| `forbidden` | 403 | Zugriff nicht von localhost |
| `read_only` | 403 | Schreibbefehl, Bridge ist schreibgeschützt |
| `unknown_method` | 404 | Methode gibt es nicht |
| `not_found` | 404 | Titel, Wissenselement, Kategorie oder Datei nicht gefunden |
| `no_project` | 409 | Kein Projektfenster offen |
| `bad_request` | 400 | Parameter fehlt oder ungültig |
| `api_mismatch` | 500 | Citavi-Version hat erwarteten Typ/Member nicht |
| `citavi_error` | 500 | Ausnahme innerhalb von Citavi |

## Methoden

`system.methods` liefert die aktuelle Liste mit Kurzbeschreibung direkt aus dem Add-on.

| Methode | Schreibt | Parameter |
|---|---|---|
| `system.ping` | | (ohne offenes Projekt: Hinweis und Projektliste) |
| `system.methods` | | |
| `projects.list` | | liefert `{open[], known[]}`: offene Projekte und Citavis Projektliste. Funktioniert ohne offenes Projekt |
| `projects.open` | ✔ | `name` (wie in `known`), `timeoutSeconds=180`. Öffnet lokale und Cloud-Projekte ohne Klick, auch aus dem Startfenster |
| `debug.describeType` | | `typeName` |
| `debug.findMembers` | | `pattern`, `limit=300` |
| `debug.inspect` | | `knowledgeItemId` oder `locationId`, `depth=3` |
| `references.types` | | |
| `references.search` | | `query?`, `categoryPath?[]`, `limit=25` |
| `references.get` | | `id` |
| `references.create` | ✔ | `type`, `fields{}`, `authors[]`, `editors[]`, `periodical`, `publishers[]`, `abstract`, `keywords[]`, `categories[]` |
| `references.update` | ✔ | `id` + wie `create` |
| `categories.list` | | |
| `categories.create` | ✔ | `path` |
| `categories.assign` | ✔ | `categories[]`, `referenceIds[]`, `knowledgeItemIds[]`, `remove=false`, `create=true` |
| `keywords.list` | | |
| `keywords.assign` | ✔ | `keywords[]`, `referenceIds[]`, `knowledgeItemIds[]`, `remove=false` |
| `knowledgeItems.list` | | `referenceId?`, `categoryPath?[]`, `query?`, `limit=50` |
| `knowledgeItems.create` | ✔ | `referenceId`, `type`, `text`, `coreStatement`, `pageRange`, `categories[]`, `keywords[]` |
| `knowledgeItems.update` | ✔ | `id`, `text?`, `coreStatement?`, `pageRange?`, `categories[]`, `keywords[]` |
| `attachments.add` | ✔ | `referenceId`, `path`, `mode=copy\|link`, `notes?` |
| `attachments.list` | | `referenceIds[]?`, `categoryPath?[]`, `query?`, `onlyMissing=false`, `onlyPdf=false`, `limit=500` |
| `attachments.download` | (Datei­system) | `referenceIds[]?`, `locationIds[]?`, `categoryPath?[]`, `query?`, `targetFolder?`, `naming=citation\|original`, `onlyPdf=true`, `overwrite=false`, `timeoutSeconds=300`, `limit=200` |
| `pdf.findText` | | `referenceId` oder `locationId`, `text`, `page=0`, `limit=20` |
| `pdf.highlight` | ✔ | `knowledgeItemId`, `text?`, `page?`, `occurrence=1`, `locationId?`, `color?` |
| `citationStyle.get` | | |
| `citationStyle.list` | | `query?`, `limit=200` (Citavis bekannte Stile + `*.ccs` in `citationStyleFolders`) |
| `citationStyle.set` | ✔ | `name` (oder eindeutiger Teil) oder `id` |

### Formate

- **Anhänge:** `{"locationId", "referenceId", "fileName", "storage": "local|cloud|linked|url|unknown",
  "resourceType", "availableLocally", "localPath", "address"}`.
- **Download-Ergebnis je Datei:** `status` = `downloaded` | `copied` | `already_there` | `not_available`
  (dann mit `hint` und `tried`, der Liste der versuchten Citavi-Methoden).
- **Personen:** `{"lastName": "Schuh", "firstName": "Günther"}` oder Text `"Schuh, Günther"` bzw. `"Günther Schuh"`.
- **Kategorienpfade:** `"Kapitel 2 > Methodik"` oder `["Kapitel 2", "Methodik"]`.
- **Felder (`fields`):** Namen der Citavi-Eigenschaften der Klasse `Reference`, z. B. `Title`,
  `Subtitle`, `Year`, `Doi`, `Isbn`, `Volume`, `Number`, `PageRange`, `PlaceOfPublication`, `Edition`,
  `OnlineAddress`, `Notes`. Welche es gibt: `debug.describeType` mit `typeName = "Reference"`.
- **Wissenselement-Typen:** `direct`, `indirect`, `summary`, `comment` (oder direkt der Citavi-Enum-Name).

## Beispiel

```json
{"method": "references.create", "params": {
  "type": "Book",
  "fields": {"Title": "Produktionsmanagement", "Year": "2012", "PlaceOfPublication": "Berlin"},
  "authors": ["Schuh, Günther"],
  "publishers": ["Springer"],
  "categories": ["Kapitel 2 > Grundlagen"]
}}
```

### Ab v0.3: Umbau, Pflege, Zusammenführen

Methoden mit ⚠ verlangen `confirm: true`, sonst Fehler `confirm_required`.

| Methode | Schreibt | Parameter |
|---|---|---|
| `categories.rename` | ✔ | `path`, `newName` |
| `categories.move` | ✔ | `path`, `newParent?` (Pfad, `""` = oberste Ebene), `after?` oder `position?` (`first`/`last`). Anderer Elternknoten: neu anlegen und mit `Category.Merge` überführen |
| `categories.reorder` | ✔ | `parent?`, `order[]` |
| `categories.sort` | ✔ | `parent?`, `recursive=false` |
| `categories.merge` | ✔ | `source`, `target` |
| `categories.delete` | ✔ ⚠ | `path`, `mergeInto?` (dann ohne `confirm`) |
| `categories.contents` | | `path`, `includeSubcategories=false`, `limit=200` |
| `categories.set` | ✔ | `categories[]`, `referenceIds[]`, `knowledgeItemIds[]` (ersetzt) |
| `categories.unassigned` | | `limit=200` |
| `groups.list`, `keywords.usage` | | |
| `groups.assign` | ✔ | `groups[]`, `referenceIds[]`, `knowledgeItemIds[]`, `remove=false` |
| `groups.rename`, `keywords.rename` | ✔ | `name`, `newName` |
| `groups.merge`, `keywords.merge` | ✔ | `sources[]`, `target` |
| `groups.delete`, `keywords.delete` | ✔ ⚠ | `names[]` |
| `thoughts.list` / `thoughts.create` | / ✔ | `query?` / `coreStatement`, `text?`, `categories[]`, `keywords[]`, `groups[]` |
| `tasks.list` | | `referenceId?`, `includeDone=false` |
| `tasks.create` / `tasks.update` | ✔ | `name`, `referenceId?` / `id`; `dueDate`, `importance` (Low/Middle/High), `state` (NotStarted/Started/Advanced/Done), `notes` |
| `tasks.delete` | ✔ ⚠ | `ids[]` |
| `references.importIdentifier` | ✔ | `identifier` (DOI/ISBN/PMID/arXiv), `categories[]`, `keywords[]`, `groups[]`, `allowDuplicate=false` |
| `references.duplicates` | | `useCitaviComparer=true` |
| `references.merge` | ✔ ⚠ | `keepId`, `duplicateIds[]`, `overrideData=false`. Kopiert Wissenselemente, Anhänge, Aufgaben mit Citavis Klon-Logik, dann `MergeReference`; löscht die Dublette nur, wenn alle Wissenselemente angekommen sind |
| `references.delete` | ✔ ⚠ | `ids[]` |
| `knowledgeItems.delete` | ✔ ⚠ | `ids[]`, `removeHighlights=true` |
| `references.copyToProject` | ✔ | `referenceIds[]`, `targetProject`, `copyCategories=false`, `copyKeywords`, `copyGroups`, `copyTasks`, `copyAttachments` (je `true`) |
| `knowledgeItems.copyTo` | ✔ | `knowledgeItemIds[]`, `targetReferenceId`, `targetProject?`, Kopier-Schalter wie oben |
| `project.export` | | `referenceIds[]?`, `includeKnowledgeItems=true` (inkl. Markierungs-Quads) |
| `pdf.highlight` | ✔ | zusätzlich `quads[[page, x1, y1, x2, y2], …]` statt Textsuche |

Neue Felder an Titeln: `Rating`, `HasLabel1`/`HasLabel2` (rote/blaue Marke), `CustomField1` bis `CustomField9`.
Wissenselemente: `groups[]`, `relevance`.

### Ab v0.4: Quellen lesen, Querverweise, Organizer, Listen, Projekte anlegen

| Methode | Schreibt | Parameter |
|---|---|---|
| `projects.create` | ✔ | `name`, `type` (`cloud`/`local`, Standard `cloud`), `folder` (nur `local`: übergeordneter Ordner, Standard ist Citavis Projektordner, das Projekt bekommt einen eigenen Unterordner). Ohne offenes Projekt nutzbar |
| `pdf.text` | | `referenceId` oder `locationId`, `fromPage=1`, `toPage=0`, `maxChars`. Seiten mit Text und, falls im PDF hinterlegt, gedruckter Seitenzahl |
| `links.list` / `links.create` / `links.delete` ⚠ | / ✔ / ✔ | `referenceId` / `sourceId`, `targetId`, `relation?`, `notes?`, `pages?` / `linkIds[]` |
| `organizer.list` | | `path` |
| `organizer.reorder` | ✔ | `path`, `order[]` (IDs). Citavi: `Move(item, target)` setzt hinter das Ziel |
| `organizer.addSubheading` | ✔ | `path`, `text`, `afterKnowledgeItemId?` |
| `savedSearches.list` / `.create` / `.delete` ⚠ | / ✔ / ✔ | – / `name`, `query`, `matchCase`, `matchWholeWord` / `names[]` |
| `lists.get` | | `kind` (`persons`, `periodicals`, `publishers`, `seriesTitles`), `query?`, `onlyUnused` |
| `lists.update` / `lists.merge` / `lists.deleteUnused` ⚠ | ✔ | `kind` + `id`, `fields{}` / `targetId`, `sourceIds[]` / `ids[]?` |
| `references.exportFile` | | `path`, `format` (`ris`), `referenceIds[]?`, `categoryPath?` |

RIS- und BibTeX-Import sowie BibTeX-Export laufen im MCP-Server (`bibio.py`): Citavis `Importer` liefert in 6.20
ohne seinen Vorschaudialog keine Titel, der BibTeX-Exportfilter schreibt ohne Dialog eine leere Datei.
