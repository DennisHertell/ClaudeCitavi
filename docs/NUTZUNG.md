# Nutzung: So arbeitest du mit Claude und Citavi

## Das Prinzip in einem Satz

Citavi läuft auf deinem Windows-Rechner, Claude (Desktop-App oder Claude Code **auf demselben
Rechner**) bekommt über das Add-on Zugriff darauf, und du sagst Claude in normaler Sprache, was in
Citavi passieren soll.

```
Du ──Chat──► Claude Desktop ──► citavi-mcp ──► Add-on in Citavi ──► lokales Projekt oder Cloud-Projekt
```

## Einmalig einrichten

1. Installation nach [INSTALLATION.md](INSTALLATION.md) (ein Skript, ca. 10 Minuten).
2. Claude Desktop neu starten. Unter *Einstellungen > Entwickler* (bzw. im Werkzeug-Menü des
   Chat-Eingabefelds) erscheint der Server **citavi**.

## Jedes Mal

1. **Citavi starten und das Projekt öffnen**, lokal oder Cloud. Citavi darf minimiert im Hintergrund laufen.
2. **Claude Desktop** öffnen und loslegen. Claude fragt beim ersten Werkzeugaufruf, ob es das Werkzeug
   benutzen darf. Für Lesewerkzeuge kannst du *Immer erlauben* wählen; bei schreibenden Werkzeugen
   lohnt sich am Anfang die Einzelbestätigung.

> Diese Verbindung funktioniert nur von Claude **auf deinem Rechner** aus. Eine Claude-Sitzung in der
> Cloud (wie die, in der dieses Repository entwickelt wird) kommt nicht an dein Citavi.

## Beispiele, was du sagen kannst

**Verbindung und Überblick**
- „Prüfe die Verbindung zu Citavi."
- „Welche Projekte sind in Citavi offen?" → „Arbeite mit dem Projekt *Diss Cloud*."

**Literatur**
- „Importiere die DOI 10.1016/j.procir.2021.11.123 und ordne sie Kapitel 2 > Variantenmanagement zu."
- „Lege das Buch *Produktionsmanagement* von Günther Schuh, Springer, Berlin 2012 an."
- „Suche alle Titel von Schuh nach 2015 und zeige mir die Liste."
- „Ergänze beim Titel *…* das Schlagwort *Komplexitätskosten*."

**Wissenselemente und Kategorien**
- „Lege zu *Schuh 2012* ein wörtliches Zitat von Seite 45 an: ‚…', Kernaussage ‚Variantenvielfalt treibt Gemeinkosten', Kategorie *AP1 > Kostenwirkung*."
- „Lege die Kategorien *AP2 > Maßnahmen > Konstruktion* und *AP2 > Maßnahmen > Prozess* an."
- „Ordne alle Wissenselemente mit dem Wort *Plattform* der Kategorie *AP2 > Maßnahmen* zu."

**PDFs**
- „Hänge C:\Users\&lt;Name&gt;\Downloads\paper.pdf an den Titel *…* an."
- „Welche PDFs im Cloud-Projekt fehlen auf diesem Rechner?"
- „Lade alle PDFs aus Kategorie *AP1* herunter und lege sie in D:\Diss\Literatur\AP1 ab."
- „Lade alle PDFs herunter, die mein Kollege hochgeladen hat, mit Dateinamen *Autor Jahr - Titel*."

**Zitationsstil**
- „Welcher Zitationsstil ist eingestellt?" / „Stelle auf den Stil *APA 7th* um." (experimentell)

## Cloud-Projekte

- Das Add-on arbeitet mit **lokalen und Cloud-Projekten** gleich, solange das Projekt in der
  Citavi-Desktop-App geöffnet ist. Du musst in Citavi mit deinem Citavi-Account angemeldet und
  Mitglied des Cloud-Projekts sein.
- Neue Titel, Wissenselemente und Anhänge, die Claude anlegt, synchronisiert Citavi wie gewohnt in die Cloud.
- **PDFs von anderen Rechnern oder Personen:** Citavi lädt Cloud-Anhänge normalerweise erst beim
  Öffnen herunter. `download_attachments` bittet Citavi, das sofort zu tun, und kopiert die Datei
  dann in deinen Zielordner. Ergebnis je Datei:
  - `downloaded`: war nur in der Cloud, jetzt lokal
  - `copied`: war schon lokal, wurde kopiert
  - `already_there`: liegt schon identisch im Zielordner
  - `not_available`: Citavi hat die Datei nicht geliefert. Die PDF einmal in Citavi öffnen (dann lädt
    Citavi sie) und den Befehl wiederholen. Bitte Meldung in Issue posten, damit der Automatismus
    verbessert werden kann.
- Standard-Zielordner: `Dokumente\ClaudeCitavi\Downloads`. Ändern in
  `%APPDATA%\ClaudeCitavi\bridge.json`, z. B. `"downloadFolder": "D:\\Diss\\Literatur"`.
- **Nicht möglich:** Zugriff auf Cloud-Projekte, ohne dass die Citavi-App läuft. Citavi bietet dafür
  keine offizielle Schnittstelle. Auch *Citavi Web* (nur im Browser) wird nicht unterstützt.

## Gute Gewohnheiten

- **Sicherung** vor größeren Aktionen (Citavi: *Datei > Sicherung erstellen*). Bei Cloud-Projekten
  stellt Citavi zusätzlich eigene Sicherungen bereit.
- Erst **suchen lassen, dann anlegen** („Gibt es den Titel schon?"). Claude ist angewiesen, Dubletten
  zu prüfen.
- **Warnungen ernst nehmen.** Meldet Claude `warnings`, wurde etwas nur teilweise übernommen.
- Nur lesen lassen: `"readOnly": true` in `bridge.json` (Downloads bleiben erlaubt, weil sie das
  Projekt nicht verändern).

## Wenn etwas nicht klappt

1. „Prüfe die Verbindung zu Citavi." Die Fehlermeldung sagt meist schon, woran es liegt.
2. Tabelle *Fehlerbehebung* in [INSTALLATION.md](INSTALLATION.md).
3. Issue anlegen (Vorlage *Fehler melden*) mit der Meldung und den letzten Zeilen aus
   `%APPDATA%\ClaudeCitavi\bridge.log`.
