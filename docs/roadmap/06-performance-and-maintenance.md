# Stufe 6 — Performance und Wartbarkeit

## Arbeitspakete

- Cache-Frische pro Quelle und Kategorie anzeigen.
- Refreshes für Identität, Lodestone, FFXIVCollect, Achievements, Collections
  und FC selektiv ausführen.
- Gleiche Spieleranfragen zusammenfassen.
- Cancellation bei Spielerwechsel und Plugin-Stop durchreichen.
- Retry und Rate Limit unterscheiden.
- Pure Services für Freshness, Queue-Priorisierung, History-Diff und
  Notification-Policy aus UI und Lifecycle lösen.
- Debugwerte für Refreshdauer, Cache-Hit-Rate, Queue-Wartezeit und Fehlerrate.

## Akzeptanz

Ein Teil-Refresh lädt keine unnötigen Kategorien. Domain-Logik ist ohne Dalamud
testbar und Cancellation beendet laufende Requests sauber.

