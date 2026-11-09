# ADR 0001: Citavi-Add-on als Bridge, MCP-Server als Schnittstelle zu Claude

- **Status:** angenommen
- **Datum:** 2026-11-02

> ADR = Architecture Decision Record. Kurze Notiz, *warum* etwas so gebaut ist. Neue
> Grundsatzentscheidungen bekommen eine neue Datei mit fortlaufender Nummer; alte werden nicht
> gelöscht, sondern ggf. als „ersetzt durch ADR 000X" markiert.

## Kontext

Claude soll Citavi im Hintergrund bedienen: Literatur anlegen, Wissenselemente, Kategorien, PDFs,
Zitationsstil. Citavi bietet keine offizielle API für externe Programme. Möglich sind:

| Option | Bewertung |
|---|---|
| A. Direkt in die SQLite-Projektdatei schreiben | Gefährlich bei laufendem Citavi (Cache, Sperren, Beschädigung). Keine Cloud-Projekte. Lesen wäre okay. |
| B. Oberfläche fernsteuern (Klicks simulieren) | Sehr fragil, langsam, blockiert den Rechner. |
| C. Citavi-Makros (C#-Makroeditor) | Läuft im richtigen Kontext, muss aber manuell gestartet werden. Kein Dauerbetrieb. |
| D. **Citavi-Add-on mit lokalem Server** | Läuft dauerhaft in Citavi, nutzt das echte Objektmodell, Änderungen sofort sichtbar. Bauen und Installieren nötig. |

Für Claude gibt es zwei Anbindungswege: eine eigene API oder das **Model Context Protocol (MCP)**,
den Standard, über den Claude Desktop und Claude Code Werkzeuge einbinden.

## Entscheidung

Option D mit zwei getrennten Teilen:

1. **Bridge-Add-on (C#)** in Citavi: kleiner HTTP-Server auf `127.0.0.1`, generisches RPC-Protokoll,
   so wenig Logik wie nötig.
2. **MCP-Server (Python)** außerhalb von Citavi: Werkzeugbeschreibungen für Claude, Komfortfunktionen
   (z. B. DOI-Import), leicht zu testen und zu ändern, ohne Citavi neu zu starten.

## Konsequenzen

- Gut: Änderungen am MCP-Teil (Beschreibungen, neue Kombi-Werkzeuge) brauchen keinen Neustart von Citavi.
- Gut: Der MCP-Teil ist ohne Citavi automatisiert testbar (simulierte Bridge).
- Gut: Die Bridge ist unabhängig von Claude und könnte auch von anderen Werkzeugen genutzt werden.
- Schlecht: Zwei Sprachen, zwei Installationsschritte (das Installationsskript fasst sie zusammen).
- Schlecht: Abhängig vom internen, undokumentierten Objektmodell von Citavi. Abgefedert durch
  Reflection und das Diagnosewerkzeug `describe_citavi_type`.
- Später möglich: Option A als reiner Lesezugriff, wenn Citavi nicht läuft.
