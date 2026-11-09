# Sicherheit

## Sicherheitslücke melden

Bitte Sicherheitslücken **nicht** als öffentliches Issue melden, sondern vertraulich über
*Security > Report a vulnerability* in diesem Repository. Ich antworte in der Regel innerhalb einer Woche.

## Was als Sicherheitsproblem zählt

- Zugriff auf die Bridge ohne gültigen Schlüssel oder von einer anderen Adresse als `localhost`
- Ausführen von Code oder Lesen von Dateien über die Bridge oder den MCP-Server, das über die beschriebenen
  Funktionen hinausgeht
- Manipulierte Pakete oder Prüfsummen in den Releases

Grenzen der Bridge, die bekannt und beschrieben sind (zum Beispiel, dass Programme unter demselben Windows-Konto die
`bridge.json` lesen können), stehen in [docs/IT-INSTALLATION.md](docs/IT-INSTALLATION.md).

## Unterstützte Versionen

Sicherheitskorrekturen gibt es für die jeweils neueste Version.
