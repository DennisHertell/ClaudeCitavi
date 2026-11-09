# Mitwirken

Beiträge sind willkommen: Fehlermeldungen, Ideen und Pull Requests.

## So läuft ein Beitrag

1. Für Fehler oder Ideen zuerst ein [Issue](../../issues) anlegen. Vorlagen für Fehler und Funktionen gibt es.
2. Das Repository forken und einen Branch anlegen, zum Beispiel `feat/gruppen-export` oder `fix/pdf-markierung`.
3. Änderungen machen. Vor dem Pull Request lokal prüfen:
   ```bash
   cd mcp-server && ruff check . && ruff format --check . && pytest
   ```
   Für das Add-on zusätzlich `dotnet build bridge/src/ClaudeCitavi.Bridge` gegen ein installiertes Citavi 6 oder die
   CI-Stubs (siehe [docs/ENTWICKLUNG.md](docs/ENTWICKLUNG.md)).
4. Pull Request gegen `main` öffnen und die Vorlage ausfüllen, besonders: Wie und auf welcher Citavi-Version getestet?
5. Die automatischen Prüfungen laufen nach Freigabe durch den Maintainer. Gemergt wird nur, was der Maintainer
   geprüft und für sinnvoll befunden hat (Squash-Merge).

## Regeln

- Neue Bridge-Methode: Handler in `bridge/src/ClaudeCitavi.Bridge/Handlers`, Eintrag in `docs/BRIDGE-PROTOKOLL.md`,
  MCP-Werkzeug in `mcp-server/src/citavi_mcp/server.py`, Nachbildung in `mcp-server/tests/conftest.py` und Test.
- Citavi-API nur über die Reflection-Helfer in `Cv.cs` ansprechen, damit das Add-on über Citavi-Versionen hinweg baut.
- Die Zitierregeln in [docs/ZITIERREGELN.md](docs/ZITIERREGELN.md) werden nicht aufgeweicht.
- Jede Änderung in `CHANGELOG.md` unter *Unreleased* eintragen. Commits nach Conventional Commits
  (`feat(bridge): ...`, `fix(mcp): ...`, `docs: ...`).
- Keine echten Projektdaten, Namen oder Zugangsdaten in Code, Tests, Screenshots oder Issues.

Mit einem Beitrag stimmst du zu, dass er unter der [MIT-Lizenz](LICENSE) des Projekts steht.
