# Architektur

## Ziel

Claude soll alles, was man in Citavi per Klick erledigt, im Hintergrund ausführen können, während
Citavi läuft. Änderungen sollen sofort in der Citavi-Oberfläche sichtbar sein und Citavis eigene
Mechanismen (Speichern, Rückgängig, Cloud-Synchronisation) nicht umgehen.

## Komponenten

```
┌───────────────────────── Dein Windows-Rechner ─────────────────────────┐
│                                                                        │
│  Claude Desktop / Claude Code                                          │
│        │ startet als Unterprozess, spricht MCP über stdin/stdout       │
│        ▼                                                               │
│  citavi-mcp (Python)                                                   │
│   • beschreibt die Werkzeuge für Claude (Namen, Parameter, Hilfetexte) │
│   • Komfortfunktionen, z. B. DOI über Crossref auflösen                │
│        │ HTTP POST http://127.0.0.1:8765/rpc                           │
│        │ Header: Authorization: Bearer <Token aus bridge.json>         │
│        ▼                                                               │
│  Citavi.exe                                                            │
│   └─ ClaudeCitavi.Bridge.dll (Add-on)                                  │
│       • lokaler HTTP-Server, nur Loopback                              │
│       • führt jeden Befehl auf dem UI-Thread von Citavi aus            │
│       • greift über das Citavi-Objektmodell auf das Projekt zu         │
└────────────────────────────────────────────────────────────────────────┘
```

| Komponente | Sprache | Ort im Repo | Läuft in |
|---|---|---|---|
| Bridge-Add-on | C#, .NET Framework 4.8 | `bridge/src/ClaudeCitavi.Bridge` | Citavi-Prozess |
| MCP-Server | Python ≥ 3.10 | `mcp-server/src/citavi_mcp` | eigener Prozess, von Claude gestartet |
| Gemeinsame Konfiguration | JSON | `%APPDATA%\ClaudeCitavi\bridge.json` | wird vom Add-on beim ersten Start erzeugt |

## Wichtige Designentscheidungen

**Add-on statt direktem Datenbankzugriff.** Lokale Citavi-Projekte liegen als SQLite-Datei vor. Direkt
hineinzuschreiben, während Citavi geöffnet ist, würde Citavis Zwischenspeicher umgehen und kann das
Projekt beschädigen. Cloud-Projekte haben gar keine lokale Datei. Ein Add-on arbeitet dagegen mit genau
den Objekten, die auch die Oberfläche nutzt. Details: [ADR 0001](adr/0001-addon-bridge-und-mcp.md).

**UI-Thread.** Das Objektmodell von Citavi ist nicht threadsicher. Jeder Befehl wird deshalb per
`Form.Invoke` auf dem UI-Thread ausgeführt. Folge: Ein offener modaler Dialog in Citavi kann Befehle
verzögern; der MCP-Server meldet dann nach 30 s eine Zeitüberschreitung.

**Reflection statt fester Typen.** Citavi hat keine offiziell dokumentierte Add-on-API, und die interne
API unterscheidet sich zwischen Versionen. Außer dem Einstiegspunkt (`CitaviAddOn<MainForm>`) greift
das Add-on deshalb per Reflection zu (`Cv.cs`). Vorteil: Das Add-on lässt sich immer bauen; fehlt ein
Feld in einer Citavi-Version, gibt es eine verständliche Fehlermeldung oder eine Warnung statt eines
Absturzes. Mit dem Werkzeug `describe_citavi_type` lässt sich die echte API vor Ort nachsehen.

**Warnungen statt Abbruch.** Beim Anlegen eines Titels werden alle Felder einzeln gesetzt. Klappt ein
Feld nicht, wird der Titel trotzdem angelegt und die Antwort enthält `warnings`. Claude soll diese dem
Nutzer melden.

**Aktives Projekt.** Sind mehrere Projekte geöffnet, arbeitet die Bridge mit dem Fenster, das zuletzt
aktiv war.

**Start ohne Projekt.** Citavi erzeugt das Add-on beim Programmstart. Die Bridge startet schon dann und nutzt
das Startfenster als Ziel für `Invoke`. So kann Claude mit `projects.open` Projekte öffnen, ohne dass jemand
klickt (`Program.OpenProjectAsync` mit dem `ConnectionString` aus `KnownProjects`). Neue Projektfenster werden
beim Öffnen erkannt (`OnHostingFormLoaded` und `Application.OpenForms`).

## Cloud-Projekte

Citavi-Cloud-Projekte haben keine lokale Projektdatei und Citavi bietet keine öffentliche Cloud-API.
Das Add-on löst das, indem es **in der Desktop-App** arbeitet: Ist ein Cloud-Projekt geöffnet, sieht
das Add-on dasselbe Objektmodell wie bei einem lokalen Projekt. Citavi synchronisiert Änderungen
selbst mit der Cloud, mit dem Login des Nutzers.

**Mehrere Projekte:** Das Add-on merkt sich alle offenen Projektfenster. Jede Anfrage kann über das
Feld `project` ein Projekt per Namen wählen (MCP-Werkzeug `select_project`), sonst gilt das zuletzt
aktive Fenster.

**Anhänge aus der Cloud:** Dateien, die von anderen Rechnern oder Personen hochgeladen wurden, lädt
Citavi normalerweise erst beim Öffnen. `attachments.download` läuft in zwei Schritten:

1. *UI-Thread:* Für jeden fehlenden Anhang wird Citavi gebeten, ihn herunterzuladen. Die genaue
   Methode ist undokumentiert; das Add-on sucht per Reflection nach `Download*`-Methoden an
   `Location`/`LinkedResource` und passenden Erweiterungsmethoden und protokolliert, was es versucht hat.
2. *Arbeits-Thread:* Auf die Downloads warten (ohne Citavi zu blockieren), Pfade neu auflösen und
   die Dateien in den Zielordner kopieren.

Liefert Citavi eine Datei nicht, meldet das Werkzeug `not_available` mit Diagnose. Mit
`debug.findMembers` (Werkzeug `find_citavi_members`, z. B. Muster „Download") lässt sich die passende
Citavi-Methode vor Ort finden und danach fest einbauen.

## Sicherheit

- Der HTTP-Server hört nur auf `localhost`/`127.0.0.1` und lehnt andere Adressen ab.
- Jeder Aufruf braucht das zufällige Token aus `bridge.json` (256 Bit, beim ersten Start erzeugt).
  Damit können andere Programme oder Webseiten im Browser nicht unbemerkt auf Citavi zugreifen.
- `"readOnly": true` in `bridge.json` sperrt alle schreibenden Befehle.
- Löschen ist in v0.1 bewusst nicht vorgesehen.

## Grenzen (Stand v0.1)

- Funktioniert nur mit der **Windows-Desktop-Version** von Citavi (lokale und Cloud-Projekte),
  nicht mit Citavi Web.
- Citavi muss laufen und das Projekt geöffnet haben (minimiert reicht).
- Download von Cloud-Anhängen ist ungeprüft (siehe oben).
- Die Bridge ist für Citavi 6 geschrieben. Citavi 7 sollte weitgehend gleich funktionieren, ist aber
  nicht geprüft.
