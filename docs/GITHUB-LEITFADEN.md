# GitHub-Leitfaden für dieses Projekt

Kurz erklärt, wie dieses Repository organisiert ist, damit alles nachvollziehbar bleibt. Claude hält
sich an diese Regeln; du musst sie nicht auswendig können.

## Die wichtigsten Begriffe

| Begriff | Bedeutung | Wo |
|---|---|---|
| **Repository** | Projektordner mit vollständiger Versionsgeschichte | github.com/DennisHertell/ClaudeCitavi |
| **Commit** | Ein gespeicherter Änderungsschritt mit Beschreibung | Reiter *Code > Commits* |
| **Branch** | Parallele Arbeitslinie. `main` ist der stabile Stand. | Auswahlfeld oben links im Reiter *Code* |
| **Issue** | Aufgabe, Idee oder Fehlermeldung | Reiter *Issues* |
| **Pull Request (PR)** | Vorschlag, einen Branch in `main` zu übernehmen. Hier wird geprüft und freigegeben. | Reiter *Pull requests* |
| **Label** | Schlagwort für Issues/PRs, z. B. `bug`, `bridge` | an Issues und PRs |
| **Milestone** | Sammlung von Issues für eine Version, z. B. *v0.1 Inbetriebnahme* | *Issues > Milestones* |
| **CI (Actions)** | Automatische Prüfungen bei jedem Push (Tests, Build) | Reiter *Actions*, grüner Haken am PR |
| **Release** | Markierter, installierbarer Versionsstand, z. B. `v0.1.0` | rechte Spalte *Releases* |

## Ablauf einer Änderung

```
Issue  ──►  Branch  ──►  Commits  ──►  Pull Request  ──►  CI grün + deine Freigabe  ──►  main
 #12        claude/…     feat: …        „Closes #12"        ✔                            ✔ Issue zu
```

1. **Jede Aufgabe beginnt als Issue.** So ist später nachvollziehbar, *warum* etwas geändert wurde.
2. **Gearbeitet wird nie direkt auf `main`**, sondern auf einem Branch (Claude nutzt `claude/...`).
3. **Commits** folgen den *Conventional Commits*:
   `feat:` neue Funktion · `fix:` Fehlerbehebung · `docs:` Dokumentation · `test:` Tests ·
   `ci:` automatische Prüfungen · `chore:` Aufräumen · `refactor:` Umbau ohne neue Funktion.
   Beispiel: `feat(bridge): add knowledge item creation`.
4. **Pull Request** mit Beschreibung nach Vorlage. `Closes #12` im Text schließt das Issue automatisch beim Zusammenführen.
5. **CI muss grün sein.** Danach gibst du frei: *Merge pull request* → **Squash and merge**
   (fasst alle Commits zu einem sauberen Eintrag in `main` zusammen).
6. Der Branch wird danach gelöscht (Knopf *Delete branch*).

## Versionen

- **Semantic Versioning:** `MAJOR.MINOR.PATCH`. Vor 1.0 gilt: MINOR = neue Funktionen, PATCH = Korrekturen.
- Jede Änderung wird in [`CHANGELOG.md`](../CHANGELOG.md) unter *Unreleased* eingetragen.
- Für ein Release wird *Unreleased* zur Versionsnummer, ein Tag `vX.Y.Z` gesetzt und ein GitHub-Release erstellt.

## Labels

| Label | Wofür |
|---|---|
| `bug` | Etwas funktioniert nicht |
| `enhancement` | Neue Funktion oder Verbesserung |
| `documentation` | Doku |
| `bridge` | betrifft das Citavi-Add-on (C#) |
| `mcp-server` | betrifft den MCP-Server (Python) |
| `needs-citavi-test` | muss auf einem echten Citavi getestet werden |
| `good first issue` | kleine, gut abgegrenzte Aufgabe |

## Einmalige Einstellungen, die nur du setzen kannst

Diese Einstellungen erfordern Admin-Rechte im Browser. Empfehlung:

1. **`main` schützen:** *Settings > Branches > Add branch ruleset* (bzw. *Add rule*)
   - Branch: `main`
   - ✔ *Require a pull request before merging*
   - ✔ *Require status checks to pass* → Checks `MCP server (Python)` und `Bridge add-on (compile check)` auswählen
     (erscheinen nach dem ersten CI-Lauf)
   - ✔ *Block force pushes*
2. **Nur Squash-Merge:** *Settings > General > Pull Requests*: nur *Allow squash merging* anhaken,
   ✔ *Automatically delete head branches*.
3. **Milestones anlegen** (*Issues > Milestones > New milestone*): `v0.1 Inbetriebnahme`,
   `v0.2 PDF-Markierungen`, `v0.3 Komfort`. Claude ordnet die Issues dann zu.
4. Optional: *Settings > Code security*: *Dependabot alerts* und *Secret scanning* einschalten.

## Was du konkret tust

- Fehler oder Idee? → *Issues > New issue* und Vorlage wählen, oder Claude bitten, das Issue anzulegen.
- Pull Request ansehen: Reiter *Files changed* zeigt jede Änderung. Fragen als Kommentar an die Zeile.
- Freigeben: *Squash and merge*.
- Stand auf deinen Rechner holen: im Repository-Ordner `git pull`.
