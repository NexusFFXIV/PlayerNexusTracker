# PlayerNexusTracker – vollständiger Implementierungsplan

## Ziel

Alle Roadmap-Stufen werden inkrementell umgesetzt. `NexusKit.Core` bleibt
Dalamud-frei; Queue, Ergebnisse und Run-Daten gehören in `NexusKit.Modules`,
Persistenz in `InternalData`, UI und Einstellungen in
`PlayerNexusTracker.Plugin`. Jede Änderung erhält einen Feature-Branch, Tests,
Build, Review und einen eigenen PR. `.gitignore` darf mitcommittet werden;
Lock-Dateien nur bei einer beabsichtigten Dependency-Änderung.

## Reihenfolge und Detailaufgaben

### Phase 0 – Stabilisierung

1. Test- und Build-Baseline dokumentieren; die bekannte ExternalData-
   Referenzassembly-Blockade separat als CI-Infrastrukturproblem markieren.
2. Regressionstests für `HomeWorldId`, History-Deduplizierung und Queue-
   Zustände (`Idle`, `Queued`, `Waiting`, `Exhausted`) ergänzen.
3. `.gitignore`-Änderungen übernehmen und Checkpoint A ausführen.

**Abnahme:** Release-Builds und alle ausführbaren Tests grün; keine bestehende
History- oder Retry-Semantik verschlechtert.

### Phase 1 – Refresh-Ergebnis und Quellenstatus

1. In `NexusKit.Modules.PlayerEnrichment` die Dalamud-freien Records
   `RefreshOutcome` und `RefreshSourceStatus` einführen. Sie enthalten
   Kategorie, ContentId, Status (`Succeeded`, `Failed`, `Skipped`,
   `Cancelled`), Versuch, Fehlercode, Start/Ende, letzten Erfolg und nächsten
   Versuch.
2. `PlayerRefreshQueueService` erzeugt genau ein Ergebnis je verarbeitetem
   Eintrag; bestehende Events bleiben kompatibel.
3. Plugin zeigt Status, Versuch, Backoff und nächsten Versuch an; neue Texte
   kommen aus EN/DE-Ressourcen.

**Tests:** Statusmatrix, Cancellation, Cooldown, MaxAttempts und UI-Adapter.

### Phase 2 – Run-Logs und Diagnoseexport

1. Additive `InternalData`-Migration für `RefreshRun` und `RefreshRunItem`.
2. Run-Lifecycle für manuelle/zusammengefasste Refreshes, atomischer Abschluss
   auch bei Teilfehlern.
3. Begrenzte Retention (zunächst 20 Runs) über Maintenance.
4. JSON-Diagnoseexport mit Schema-Version, anonymisierten IDs und Allowlist;
   keine vollständigen Namen, Profile oder Rohantworten.
5. Export/Kopieren in der UI.

**Tests:** Upgrade-/Neuinstallationsmigration, Retention, Teilfehler,
JSON-Roundtrip und Negativtest auf private Felder.

### Phase 3 – Localization

1. Einstellung `Auto`, `GameLanguage`, `English`, `German`; unbekannte Sprache
   fällt auf Englisch zurück.
2. Laufzeitwechsel ohne Neustart durch Cache-Invalidierung.
3. Development-Audit für fehlende EN/DE-Schlüssel; Produktion fällt sicher
   zurück.
4. Plural- und lokale Datums-/Zeit-Helfer.

**Tests:** Sprachmatrix, Laufzeitwechsel, Missing-Key, Plural und Datum.

### Phase 4 – Historie und Vergleiche

1. Reinen Snapshot-Diff-Service mit `Added`, `Removed`, `Changed` und
   normalisierten Werten implementieren.
2. History-Abfrage um Kind-/Zeitraumfilter, stabile Sortierung und `HasMore`
   erweitern.
3. Timeline für Name, Home World, FC, Suchkommentar und vorhandene
   Collection-/Achievement-Änderungen.
4. History-UI erhält Filter, Snapshot-Auswahl und „seit letztem Scan“.
   Legacy-Einträge bleiben lesbar; keine destructive Migration.

**Tests:** Null/Leerwerte, identische Snapshots, Mehrfachänderungen, Limits.

### Phase 5 – Benachrichtigungen

1. Typisierte Ereignisse für Refresh, History, FC und Collections; bestehende
   `ChatNotifications`-Registry bleibt Versandpunkt.
2. Ereignisbezogene Aktivierung, Kanal/Farbe/Priorität, Quiet Hours und
   Digest-Intervall.
3. Persistenter begrenzter Verlauf mit gelesen/ungelesen und „alle gelesen“.
4. Deduplication über Ereignisart, ContentId und Zeitfenster; Producer liefern
   Ereignisse, eine zentrale Policy entscheidet über Senden/Unterdrücken.

**Tests:** Mitternacht übergreifende Quiet Hours, Digest, Deduplizierung,
Retention, Dispose und bestehende Suppression-Regeln.

### Phase 6 – Performance und Wartbarkeit

1. Quellenbezogene Freshness-/TTL-Services auf bestehender Category-Policy;
   nur veraltete Kategorien werden eingeplant.
2. Interne aggregierte Metriken für Dauer, Queue-Länge, Cache-Hit/Miss und
   Fehler je Quelle; keine personenbezogenen Labels.
3. Cancellation bis zu externen Services weiterreichen.
4. Reine Domain-Services extrahieren, nur bei Mehrfachnutzung; Unit-Tests für
   Freshness, Planung, Grenzen und Regression gegen Vollscan.

### Phase 7 – Changelog und Release-Gates

1. Nutzerorientierte Changelog-/About-Anzeige.
2. CI-Gate für Build, fokussierte und vollständige Tests, Migrationen und
   Ressourcen-Audit.
3. README und PR-Template um Nutzerwirkung, Datenschutz, Migration und
   Rollback ergänzen.
4. Graphify-Review zum Abschluss; generated Graph-Ausgaben nicht als
   Quellcode behandeln.

## Checkpoints

| Checkpoint | Voraussetzung | Abnahme |
|---|---|---|
| A | Phase 0 | Baseline reproduzierbar |
| B | Phase 1 | Status-/Queue-Vertrag stabil |
| C | Phase 2 | Run-Log und Export migrationssicher |
| D | Phase 4 | Timeline/Diff regressionsfrei |
| E | Phase 3 | EN/DE schaltbar und vollständig |
| F | Phase 5 | Notifications dedupliziert |
| G | Phase 6–7 | Release-Gate und Review grün |

## Abhängigkeiten und Risiken

```text
Phase 0 → RefreshResult → RunSummary → Snapshot-Diff → Localization
         → Notification-Policy → Performance → Release-Gates
```

Persistence-Änderungen laufen sequenziell und idempotent. Neue APIs werden
additiv eingeführt, weil das Plugin teilweise gegen veröffentlichte NuGets
läuft. Externe Live-Abfragen gehören nicht in Unit-Tests; dafür werden Fake-
Clock, Fake-HTTP und Fake-Notification-Publisher verwendet.
