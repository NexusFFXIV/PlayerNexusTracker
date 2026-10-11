# Stufen 1–2 — Refresh-Status, Run-Logs und Diagnose

## Statusmodell

Pro Quelle und Kategorie werden `Queued`, `Running`, `Succeeded`, `Failed`,
`Stale`, `Skipped`, `Exhausted` und `Disabled` unterstützt.

## Statusarbeit

- Standardisierten `RefreshResult` mit Fehlerklasse, Quelle und Zeitpunkten
  einführen.
- Letzten Versuch, letzten Erfolg, letzten Fehler, Retry-Anzahl und nächsten
  Versuch ausgeben.
- Queue-UI und Player-Detailansicht auf dasselbe Modell umstellen.
- Lodestone und FFXIVCollect getrennt sichtbar machen.
- Nur fehlende oder veraltete Kategorien selektiv aktualisieren.

## Run-Logs

`RefreshRunSummary` beschreibt manuelle Voll- und Teil-Refreshes. Gespeichert
werden Start/Ende, Dauer, Spieleranzahl, Kategorien sowie Erfolg, Skip, Fehler
und Exhausted-Zähler. Details werden nur für Fehler und Sonderfälle persistiert.
Die Aufbewahrung ist auf die letzten 20–50 Läufe begrenzt.

## Diagnoseexport

JSON und kompakter Text enthalten Plugin-/Schema-Version, Quellenstatus,
Queue-Statistiken und letzte Fehler, aber keine vollständigen privaten
Spielerdaten.

## Akzeptanz

Ein fehlgeschlagener Refresh ist ohne Logdatei verständlich; ein manueller Retry
ändert keine bereits erfolgreichen Daten; der Export ist supporttauglich.

