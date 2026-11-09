# Zitierregeln und Arbeitsablauf für Claude in Citavi

Diese Datei ist die einzige Quelle der Regeln. Der MCP Server lädt sie beim Start in seine Anweisungen.
Andere Dateien verweisen nur hierher.

## Was zitierfähig ist

Ein Wissenselement vom Typ wörtliches Zitat, indirektes Zitat oder Zusammenfassung ist nur zulässig,
wenn alle drei Bedingungen erfüllt sind.

1. **Haupttext.** Die Stelle steht im Haupttext der Arbeit. Nicht zitierfähig sind Abstract, Kurzfassung,
   Schlüsselwörter, Vorspann vor der Einleitung, Titelblatt, Klappentext und Literaturverzeichnis.
2. **Eigene Aussage der Quelle.** Die Stelle ist eine eigene Erkenntnis, Ableitung, Feststellung oder ein
   eigenes Ergebnis der Arbeit. Ein Satz, der selbst mit einer fremden Quelle belegt ist ([12], (Müller 2019),
   Müller et al. (2019), vgl., nach, hochgestellte Fußnotenziffer), ist ein Sekundärzitat und nicht zulässig.
3. **Primärquelle.** Bei einem Sekundärzitat wird die Primärquelle ermittelt, beschafft und die Stelle dort
   markiert. Bis dahin wird die Primärquelle im Literaturregister mit Status `gesucht` und der Fundstelle in der
   Sekundärquelle vermerkt und per Querverweis (`link_references`) mit der Sekundärquelle verbunden.

Kommentare und Gedanken sind eigene Notizen und von diesen Regeln ausgenommen. Sie dürfen aber nicht als
Beleg für eine Aussage der Quelle dienen.

## Technische Durchsetzung

- `create_knowledge_item` prüft Zitate und Zusammenfassungen mit PDF Stelle automatisch (`check_citability`)
  und legt bei einem Verstoß nichts an. Die Meldung nennt den Grund und bei Sekundärzitaten den gefundenen Beleg.
- Die Prüfung ist eine Heuristik. Sie ist bewusst streng. Meldet sie fälschlich einen Verstoß, die Stelle
  selbst prüfen. Eine Ausnahme (`citability_override` mit Begründung) nur mit ausdrücklicher Zustimmung des Nutzers.
- Ohne PDF Stelle (gedrucktes Buch) gibt es keine automatische Prüfung. Dann gelten die Regeln trotzdem und
  die Prüfung erfolgt beim Lesen.

## Seitenangaben

- Die Seitenangabe im Wissenselement ist die **gedruckte Seitenzahl** der Quelle, nicht die PDF Seite.
  `create_knowledge_item` übernimmt sie automatisch, wenn sie erkennbar ist. Sonst aus Kopf- oder Fußzeile ablesen.
- Erstreckt sich die Stelle über zwei Seiten, beide angeben (`12-13`).

## Arbeitsablauf: eine Quelle einmal vollständig auswerten

Ziel: Jede Quelle wird genau einmal vollständig gelesen. Danach sind alle relevanten Stellen als
Wissenselemente in Citavi und im Index verzeichnet. Ein zweites Lesen ist nicht nötig.

1. **Vorher prüfen.** Literaturregister und Quellennotiz des Projekts lesen. Ist die Quelle schon ausgewertet,
   nicht erneut lesen. In Citavi mit `search_references` den Titel finden oder per `import_reference_by_identifier`
   anlegen, PDF mit `attach_file` anhängen.
2. **Einmal vollständig lesen** mit `get_pdf_text`, Seite für Seite.
3. **Index schreiben.** Die Quellennotiz (im Dissertationsprojekt `04 Wissen\Quellennotizen\<Schlüssel>.md`)
   bekommt eine Inhaltsübersicht: welcher Abschnitt auf welcher gedruckten Seite steht, was er aussagt und
   wofür er relevant ist (Motivation, Kapitel als Kategoriepfad). Auch Stellen, die nicht markiert werden, stehen
   hier mit einem Satz, damit die Quelle später ohne erneutes Lesen auffindbar ist.
4. **Auswählen, nicht sammeln.** Markiert werden nur Stellen, die eine Aussage der Arbeit tragen können:
   zentrale Ergebnisse, Definitionen, Modelle, belastbare Zahlen, klare Abgrenzungen. Keine Wiederholungen,
   keine Einleitungsfloskeln, keine Beispiele ohne eigene Aussage. Richtwert: wenige Stellen je Quelle, bei
   Kernquellen mehr. Lieber eine Stelle zu wenig als eine schwache.
5. **Jede Stelle prüfen** mit `check_citability`. Sekundärzitate nicht markieren, sondern die Primärquelle
   nach Regel 3 vermerken.
6. **Wissenselement anlegen** mit `create_knowledge_item(highlight_in_pdf=True)`. Typ in der Regel `indirect`
   (Paraphrase in eigenen Worten, Kernaussage als kurzer Satz), wörtlich nur bei Definitionen und prägnanten
   Formulierungen. Kategorie als Pfad aus dem Kategoriensystem des Projekts, bei Bedarf Schlagwörter und Gruppen.
7. **Index ergänzen.** In der Quellennotiz zu jeder markierten Stelle die Kernaussage, die gedruckte Seite,
   die Kategorie und die Citavi ID des Wissenselements eintragen. Register auf Status `verarbeitet` setzen.

## Weitere Regeln für die Arbeit in Citavi

- Vor dem Anlegen eines Titels immer auf Dubletten prüfen. IDs nie erfinden, nur aus Antworten übernehmen.
- Löschen, Zusammenführen von Dubletten und Ausnahmen von den Zitierregeln nur nach ausdrücklicher Zustimmung,
  dann `confirm=True`.
- Neue Projekte nur nach Rückfrage. Testfälle nur in den Projekten „Claude Test“ und „Claude Test Hiwi“.
- Fremde Projekte (Hiwi, Abschlussarbeit) werden nie mit ihrer Kategorienstruktur übernommen. Ablauf:
  `compare_projects`, Zuordnung auf die eigenen Kategorien vorschlagen und freigeben lassen, `merge_project` erst
  mit `dry_run=True`, dann ausführen. Auch übernommene Wissenselemente müssen die Zitierregeln erfüllen;
  Verstöße im Bericht melden.
- Ändert sich die Gliederung, den Kategorienbaum mit `move_category`, `rename_category` und `merge_categories`
  umbauen, danach mit `list_uncategorized` prüfen.
