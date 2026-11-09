# Hinweise für Claude

Projekt: Schnittstelle, über die Claude ein laufendes Citavi bedient. Überblick in `README.md`,
Architektur in `docs/ARCHITEKTUR.md`.

## Konventionen

- Dokumentation und Texte für den Nutzer auf Deutsch, Code und Code-Kommentare auf Englisch.
- Nie direkt auf `main` arbeiten. Branch, Pull Request, CI grün, Freigabe durch den Maintainer (Squash-Merge).
- Commits nach Conventional Commits (`feat(bridge): ...`, `fix(mcp): ...`, `docs: ...`).
- Jede Änderung in `CHANGELOG.md` unter *Unreleased* eintragen.
- Aufgaben als GitHub-Issues führen; PRs verweisen mit `Closes #N` darauf.
- Neue Bridge-Methode: Handler in `bridge/src/ClaudeCitavi.Bridge/Handlers`, Tabelle in
  `docs/BRIDGE-PROTOKOLL.md`, MCP-Tool in `mcp-server/src/citavi_mcp/server.py`, Nachbildung in
  `mcp-server/tests/conftest.py` und Test.
- Citavi-API nur über `Cv`-Reflection-Helfer ansprechen (keine direkten Citavi-Typen außer dem
  Add-on-Einstieg), damit das Add-on über Citavi-Versionen hinweg baut.

## Prüfen vor dem Push

```bash
cd mcp-server && ruff check . && ruff format --check . && pytest
bridge/ci-stubs/build-stubs.sh /tmp/citavi-stubs && dotnet build bridge/src/ClaudeCitavi.Bridge -p:CitaviDir=/tmp/citavi-stubs
```
