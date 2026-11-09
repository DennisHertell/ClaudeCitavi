# ClaudeCitavi: Installationsanleitung für die IT

## Worum es geht

ClaudeCitavi verbindet die Literaturverwaltung Citavi mit dem KI Assistenten Claude. Claude kann damit im geöffneten
Citavi Projekt Titel anlegen, PDFs anhängen, Wissenselemente erfassen und Kategorien pflegen, so als würde der Nutzer
in Citavi klicken. Dafür braucht es einmalig Administratorrechte. Citavi 6 lädt eigene Add-ons nur aus dem Ordner
`AddOns` neben seinem Programmordner `bin` (ermittelt aus Citavi 6.20). Add-ons aus dem Benutzerprofil lädt es nicht.

## Bezugsquelle und Prüfung

- Paket: `ClaudeCitavi-<Version>-AddOn.zip`, aktuell Version 0.4.0. Bezug über die Releases des öffentlichen GitHub
  Repositorys [DennisHertell/ClaudeCitavi](https://github.com/DennisHertell/ClaudeCitavi/releases).
- Die SHA256 Prüfsummen der Pakete stehen in den Release Notes auf GitHub, also getrennt vom Paket. Vor der Installation
  vergleichen: `Get-FileHash .\ClaudeCitavi-0.4.0-AddOn.zip`.
- Alle Python Pakete im Ordner `wheelhouse` sind mit ihrer SHA256 Prüfsumme in `wheelhouse\requirements.txt` verzeichnet.
  Die Installation bricht ab, wenn eine Datei abweicht.
- Der vollständige Quelltext des Add-ons und des MCP Servers liegt dem Paket als `source.zip` bei. Die DLL lässt sich
  daraus mit dem .NET SDK 8 nachbauen.

## Was installiert wird

| Bestandteil | Ort | Rechte |
|---|---|---|
| Citavi Add-on `ClaudeCitavi.Bridge.dll` | `C:\Program Files (x86)\Citavi 6\AddOns\` | Administrator, einmalig |
| MCP Server (Python) in eigener Umgebung | `C:\Program Files\ClaudeCitavi\mcp-server\` | Administrator, einmalig |
| Einrichtungsskript für Nutzer | `C:\Program Files\ClaudeCitavi\nutzer-einrichten.ps1` | Administrator, einmalig |
| Eintrag in Claude Desktop, mit Sicherung `.bak` | `%APPDATA%\Claude\claude_desktop_config.json` | Nutzer |
| Eintrag in Claude Code, falls installiert | `%USERPROFILE%\.claude.json` | Nutzer |
| Einstellungen mit Port und Zugangsschlüssel | `%APPDATA%\ClaudeCitavi\bridge.json`, legt das Add-on beim ersten Start an | Nutzer |
| Protokoll des Add-ons | `%APPDATA%\ClaudeCitavi\bridge.log` | Nutzer |
| Zielordner für heruntergeladene PDFs | `%USERPROFILE%\Documents\ClaudeCitavi\Downloads`, nur bei Nutzung | Nutzer |

Es gibt keinen Dienst, keine geplante Aufgabe, keinen Autostart, keinen Registry Eintrag und keine Änderung an
Systemeinstellungen.

## Voraussetzungen

- Windows 10 oder 11, 64 Bit
- Citavi 6 Desktop. Geprüft mit Version 6.20.
- Python 3.12, 64 Bit, mit dem Python Launcher `py`, installiert für alle Benutzer (Installer von python.org). Die
  Pakete im Ordner `wheelhouse` sind für Python 3.12 gebaut. Der MCP Server hängt an dieser Python Installation. Wird
  sie entfernt oder durch eine andere Hauptversion ersetzt, `it-install.ps1` erneut ausführen.
- Claude Desktop oder Claude Code mit dem Konto des Nutzers

## Installation durch die IT (einmalig je Rechner)

1. Citavi schließen.
2. Prüfsumme des Pakets mit den Release Notes vergleichen, dann das Paket entpacken.
3. PowerShell als Administrator öffnen, in den entpackten Ordner wechseln und ausführen:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\it-install.ps1
   ```
   Das Skript prüft die Prüfsumme der DLL, kopiert sie in den AddOns Ordner von Citavi, prüft Python 3.12 64 Bit und
   installiert den MCP Server in eine eigene Python Umgebung. Alle Pakete kommen aus dem Ordner `wheelhouse` und werden
   gegen ihre Prüfsummen geprüft. Der Rechner braucht dafür keinen Internetzugang. Andere Citavi Pfade mit
   `-CitaviDir "<Ordner mit Citavi.exe>"` angeben.

Von Hand, gleichwertig zum Skript:

```powershell
Get-FileHash .\AddOn\ClaudeCitavi.Bridge.dll        # mit AddOn\ClaudeCitavi.Bridge.dll.sha256 vergleichen
New-Item -ItemType Directory -Force "C:\Program Files (x86)\Citavi 6\AddOns"
Copy-Item .\AddOn\ClaudeCitavi.Bridge.dll "C:\Program Files (x86)\Citavi 6\AddOns\"
py -3.12 -m venv "C:\Program Files\ClaudeCitavi\mcp-server\.venv"
& "C:\Program Files\ClaudeCitavi\mcp-server\.venv\Scripts\python.exe" -m pip install --no-index --find-links .\wheelhouse --require-hashes -r .\wheelhouse\requirements.txt
Copy-Item .\nutzer-einrichten.ps1 "C:\Program Files\ClaudeCitavi\"
```

## Einrichtung durch den Nutzer (ohne Administratorrechte)

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\ClaudeCitavi\nutzer-einrichten.ps1"
```

Das Skript trägt den MCP Server in die Konfiguration von Claude Desktop ein und, falls installiert, in Claude Code.
Danach Claude neu starten, Citavi starten und in Claude fragen: „Prüfe die Verbindung zu Citavi.“

## Sicherheit

- Das Add-on lauscht nur auf `localhost` und `127.0.0.1`, Port 8765, und lehnt Verbindungen von anderen Adressen ab.
  Eine Freigabe in der Windows Firewall ist nicht nötig. Getestet mit einem Benutzerkonto ohne erhöhte Rechte.
- Jeder Aufruf braucht einen zufälligen Schlüssel mit 256 Bit, den das Add-on je Nutzer in `bridge.json` erzeugt.
  Der Schlüssel schützt gegen Webseiten im Browser und gegen andere Windows Konten. Programme, die unter demselben
  Nutzer laufen, können die Datei lesen. Gegen sie schützt der Schlüssel nicht.
- Auf einem Rechner mit mehreren gleichzeitig angemeldeten Nutzern (Terminalserver) braucht jeder Nutzer einen eigenen
  Port. Er lässt sich in `bridge.json` unter `"port"` ändern.
- Das Add-on baut selbst keine Verbindung ins Internet auf. Netzzugriffe entstehen nur über Citavi und auf Wunsch des
  Nutzers: Beim Anlegen eines Titels per DOI oder ISBN fragt Citavi seine üblichen Kataloge ab, beim Herunterladen von
  PDFs aus einem Cloud Projekt lädt Citavi sie mit dem Citavi Login des Nutzers aus der Citavi Cloud. Der MCP Server
  fragt für den DOI Import über Crossref `api.crossref.org` ab.
- Mit `"readOnly": true` in `bridge.json` sperrt das Add-on alle schreibenden Funktionen. Löschen und das
  Zusammenführen von Dubletten führt das Add-on nur aus, wenn der Aufruf ausdrücklich `confirm: true` enthält. Das ist
  im Add-on umgesetzt, nicht nur im Dialog mit Claude.
- Die DLL ist nicht signiert. Falls eine Schutzsoftware anschlägt, kann die IT sie über die Prüfsumme oder den Pfad
  freigeben.

## Datenschutz

Was Claude in Citavi liest (Titel, Wissenselemente, Text aus PDFs), geht über das Claude Konto des Nutzers an
Anthropic, so wie jeder andere Inhalt, den der Nutzer Claude gibt. Ob und mit welcher Kontoart Claude in der Einrichtung
für Forschungsdaten genutzt werden darf, klärt die Einrichtung unabhängig von diesem Add-on. Das Protokoll
`bridge.log` enthält Fehlermeldungen, die Titel oder Feldinhalte enthalten können. Es bleibt lokal beim Nutzer.

## Updates und Deinstallation

- **Update:** Citavi schließen und `it-install.ps1` aus dem neuen Paket ausführen. Das Skript ersetzt die DLL und baut
  die Python Umgebung neu auf. Nach einem Update von Citavi prüfen, ob der AddOns Ordner noch die DLL enthält.
- **Deinstallation durch die IT:** `ClaudeCitavi.Bridge.dll` aus dem AddOns Ordner und den Ordner
  `C:\Program Files\ClaudeCitavi` löschen.
- **Deinstallation beim Nutzer:** Eintrag `citavi` aus `%APPDATA%\Claude\claude_desktop_config.json` entfernen
  (Sicherung `.bak` daneben löschen), in Claude Code `claude mcp remove citavi --scope user`, die Ordner
  `%APPDATA%\ClaudeCitavi` und `%USERPROFILE%\Documents\ClaudeCitavi` löschen.

## Stand und Fragen

Getestet auf Windows 11 Pro mit Citavi 6.20 und Cloud Projekten. Lokale Projekte und Teamprojekte auf einem
Citavi DBServer sind nicht getestet.
Fragen und Fehler bitte als [Issue](https://github.com/DennisHertell/ClaudeCitavi/issues) melden.
