# Installation (Windows)

Drei Wege, alle mit denselben Funktionen. Welcher passt, steht in der Übersicht im [README](../README.md#welcher-weg-passt-zu-dir).
Weg B (Add-on auf dem Dienstrechner, einmalig installiert durch die IT der Einrichtung) hat eine eigene Anleitung für die
IT: [IT-INSTALLATION.md](IT-INSTALLATION.md).

Für alle Wege nötig:

| Was | Hinweis |
|---|---|
| Windows 10 oder 11, 64 Bit | |
| Citavi 6 Desktop | geprüft mit 6.20 und Cloud-Projekten |
| Python 3.12, 64 Bit | [python.org](https://www.python.org/downloads/); für Weg C genügt eine Installation für das eigene Benutzerkonto |
| Claude Desktop oder Claude Code | mit eigenem Konto |

Die Pakete liegen unter [Releases](../../../releases). Sie enthalten die fertige DLL mit SHA256-Prüfsumme und alle
Python-Pakete offline (Ordner `wheelhouse`). Eine Internetverbindung zum Installieren ist nicht nötig.

## Weg A: Add-on auf dem eigenen Rechner

Auf dem eigenen Rechner bist du deine eigene IT und folgst der [Anleitung für die IT](IT-INSTALLATION.md):

1. Citavi schließen, `ClaudeCitavi-<Version>-AddOn.zip` entpacken.
2. PowerShell **als Administrator** im entpackten Ordner öffnen und `.\it-install.ps1` ausführen. Lässt die
   Ausführungsrichtlinie das Skript nicht zu, für diesen Aufruf `powershell -ExecutionPolicy Bypass -File .\it-install.ps1`.
   Das Skript kopiert das Add-on nach `C:\Program Files (x86)\Citavi 6\AddOns` und den MCP-Server nach
   `C:\Program Files\ClaudeCitavi`.
3. Doppelklick auf `C:\Program Files\ClaudeCitavi\Claude-verbinden.cmd`. Trägt den MCP-Server in Claude Desktop und,
   falls installiert, Claude Code ein.
4. Claude neu starten, Citavi starten. Die Brücke läuft schon im Startfenster von Citavi; Projekte öffnet Claude selbst.

**Rechner ohne Bildschirmzugriff (Heimserver).** Läuft Citavi nicht, startet der MCP-Server es beim ersten Aufruf
selbst (abschaltbar mit `CITAVI_AUTOSTART=0`). Projekte, die immer offen sein sollen, in
`%APPDATA%\ClaudeCitavi\bridge.json` unter `"autoOpenProjects": ["Dissertation"]` eintragen. Wer das Add-on häufig aus
dem Quelltext aktualisiert, kann dem eigenen Benutzer einmalig Schreibrecht auf den AddOns-Ordner geben
(`icacls "<AddOns>" /grant "%USERNAME%:(OI)(CI)M"` als Administrator) und danach `scripts\deploy.ps1` nutzen.

## Weg C: Citavi-Makro auf dem Dienstrechner

Weg C startet die Brücke über die Makro-Funktion von Citavi, in der Citavi-Sitzung des Nutzers. Es wird nichts in
Programmordnern installiert und keine Systemeinstellung geändert. Das Makro ist eine lesbare Textdatei. Für den
dauerhaften Einsatz auf Dienstrechnern ist Weg B gedacht, in Absprache mit der IT. Vor der Nutzung auf einem
Dienstrechner klären, ob Citavi-Makros und Claude nach den Regeln der Einrichtung genutzt werden dürfen.
Schritt für Schritt mit Screenshots: [anleitung-makro/ANLEITUNG-MAKRO.md](anleitung-makro/ANLEITUNG-MAKRO.md).

1. `ClaudeCitavi-<Version>-Makro.zip` entpacken.
2. Doppelklick auf `Einrichten.cmd`. Das startet `einrichten.py` mit Python 3.12. Es legt Brücke und Makro in
   `%APPDATA%\ClaudeCitavi` ab, den MCP-Server in `%LOCALAPPDATA%\ClaudeCitavi` und trägt ihn in Claude ein.
3. **Bei jeder Citavi-Sitzung einmal:** Projekt öffnen, *Extras > Makro-Editor* (Alt+F11), *Datei > Öffnen*
   `%APPDATA%\ClaudeCitavi\StartClaudeBridge.cs`, *Ausführen* (F5). Meldung „ClaudeCitavi Bridge gestartet (Port 8765)“.

Unterschied zu Weg A und B: Die Brücke startet erst mit dem Makro und braucht ein offenes Projekt. Claude kann dann
weitere Projekte selbst öffnen. Nach einem Neustart von Citavi muss das Makro erneut gestartet werden.

## Aus dem Quelltext bauen (Entwickler)

Zusätzlich [.NET SDK 8](https://dotnet.microsoft.com/download), siehe [ENTWICKLUNG.md](ENTWICKLUNG.md). Citavi
schließen, dann im Repository:

```powershell
.\scripts\install.ps1                         # Weg A aus dem Quelltext (Adminrechte für den AddOns-Ordner)
.\scripts\install.ps1 -Macro                  # Weg C aus dem Quelltext
.\scripts\build-release.ps1 -OutDir .\release # beide Pakete bauen
```

Claude Code statt Claude Desktop: `claude mcp add --scope user citavi -- "<Pfad>\citavi-mcp.exe"`.

## Funktioniert es?

Schnelltest ohne Claude, in PowerShell bei laufendem Citavi:

```powershell
$cfg = Get-Content "$env:APPDATA\ClaudeCitavi\bridge.json" | ConvertFrom-Json
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($cfg.port)/rpc" `
  -Headers @{ Authorization = "Bearer $($cfg.token)" } `
  -ContentType 'application/json' -Body '{"method":"system.ping"}'
```

Erwartet: `ok = True` und der Name deines Projekts.

## Fehlerbehebung

| Symptom | Ursache / Lösung |
|---|---|
| „Citavi ist nicht erreichbar" | Citavi läuft nicht, kein Projekt offen, oder Add-on nicht geladen. Liegt `ClaudeCitavi.Bridge.dll` im `AddOns`-Ordner? Log prüfen: `%APPDATA%\ClaudeCitavi\bridge.log` |
| „Kein Token gefunden" | Citavi mit Add-on noch nie gestartet. Citavi einmal starten. |
| Meldung beim Citavi-Start „Bridge konnte nicht gestartet werden" | Port 8765 belegt. In `bridge.json` anderen `port` eintragen, Citavi neu starten. |
| Build-Fehler „Citavi not found" | `-CitaviDir` mit dem Ordner angeben, der `Citavi.exe` enthält. |
| Build-Fehler zu `CitaviAddOn` | Citavi-Version nutzt eine andere Add-on-Basisklasse. Bitte Issue anlegen (Vorlage „Fehler melden"). |
| Antwort enthält `warnings` | Einzelne Felder ließen sich nicht setzen. Meist Feldname in dieser Citavi-Version anders. Issue mit der Warnung anlegen. |
| Zeitüberschreitung | In Citavi ist ein Dialog offen. Dialog schließen. |
| Installation: „Der Prozess kann nicht auf die Datei zugreifen … citavi-mcp.exe“ | Claude Desktop läuft noch und nutzt den MCP-Server. Claude ganz beenden (auch im Infobereich) und das Skript erneut starten. |

## Deinstallation

1. `ClaudeCitavi.Bridge.dll` aus dem `AddOns`-Ordner löschen.
2. Eintrag `citavi` aus `%APPDATA%\Claude\claude_desktop_config.json` entfernen.
3. Ordner `%APPDATA%\ClaudeCitavi` löschen.

## Einstellungen in `bridge.json`

```json
{
  "port": 8765,
  "token": "…wird automatisch erzeugt…",
  "readOnly": false,
  "downloadFolder": "D:\\Diss\\Literatur",
  "citationStyleFolders": ["D:\\04_Dissertation\\Citavi 6\\Custom Citation Styles"]
}
```

Nach Änderungen Citavi neu starten.

## Nur lesen erlauben

In `%APPDATA%\ClaudeCitavi\bridge.json` `"readOnly": true` setzen und Citavi neu starten.
