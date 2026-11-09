# citavi-mcp

MCP-Server, über den Claude mit einem laufenden Citavi arbeitet. Er spricht mit dem
Citavi-Add-on `ClaudeCitavi.Bridge` (siehe `../bridge`) über `http://127.0.0.1:8765`.

Installation und Einrichtung: siehe [`docs/INSTALLATION.md`](../docs/INSTALLATION.md).

```bash
pip install -e ".[dev]"   # Entwicklung
pytest                    # Tests (laufen ohne Citavi, gegen eine simulierte Bridge)
citavi-mcp                # Server starten (stdio), normalerweise startet ihn Claude selbst
```
