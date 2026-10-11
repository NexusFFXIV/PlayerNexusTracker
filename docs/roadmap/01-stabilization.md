# Stufe 0 — Stabilisierung und Bugfixes

## Arbeitspakete

- EF-Core-Versionen der Testprojekte vereinheitlichen.
- Fehlende direkte Testabhängigkeiten ergänzen.
- Zentralen CI-Testlauf für die Root-Solution ergänzen.
- Queue-Dokumentation an Retry-Limit und Prioritäten im Code angleichen.
- Retry, Backoff, Priorität, Cancellation und Exhausted testen.
- Weltänderungen über `HomeWorldId` statt lokalisierte Namen vergleichen.
- History-Erkennung von lokalisierter Darstellung trennen.
- Legacy-History-Einträge kompatibel darstellen.
- Queue-/Event-Dispose beim Plugin-Shutdown prüfen.
- Fehlerlogs mit Kategorie, ContentId und Quelle versehen.

## Akzeptanz

- Alle Testprojekte laufen lokal und in CI ohne Package-Downgrade-Warnungen.
- Build und Tests sind getrennte CI-Schritte.
- Bestehende History-Daten bleiben lesbar.
- Kein neuer Code benötigt ECommons.

## Tests

Retry-Grenzen, Backoff, Prioritätsreihenfolge, Cancellation, Sprachwechsel,
History-Deduplizierung und Shutdown-Verhalten.

