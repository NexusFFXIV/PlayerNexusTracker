# PlayerNexusTracker roadmap

## Ziel

PlayerNexusTracker soll Refreshes, Fehler, Historie, Benachrichtigungen,
Lokalisierung und Releases nachvollziehbar machen. Bestehende Queue-, History-,
Notification- und Localization-Infrastruktur wird erweitert; ECommons wird nicht
als Kernabhängigkeit eingeführt.

## Reihenfolge

1. Stabilisierung und Regressionstests
2. Refresh-Status und Transparenz
3. Run-Logs und Diagnoseexport
4. Historie und Vergleiche
5. Localization
6. Benachrichtigungen
7. Performance und Wartbarkeit
8. Changelog und Release-Transparenz

Jede Stufe endet mit fokussierten Tests, Build, Review und einem eigenen Commit.
Gemeinsame Persistence- oder API-Änderungen werden nicht parallel umgesetzt.

## Architekturgrenzen

- `NexusKit.Core` bleibt Dalamud-frei.
- Queue, Refresh-Ergebnisse und Run-Summaries gehören in
  `NexusKit.Modules.PlayerEnrichment`.
- Persistente History-/Run-Daten gehören in `NexusKit.Modules.InternalData`.
- Plugin-spezifische UI und Regeln bleiben in `PlayerNexusTracker.Plugin`.
- Bestehendes `ILogger<T>`, `ILocalizer` und `NexusKit.ChatNotifications` werden
  weiterverwendet.
- Diagnoseexporte dürfen keine vollständigen privaten Spielerdaten enthalten.

## Kontrollpunkte

- A: Baseline, Tests und Architektur sind dokumentiert.
- B: Status-/Retry-Modell ist stabil und getestet.
- C: Run-Logs und Export funktionieren.
- D: History-Schema und Migration sind stabil.
- E: Sprachwechsel und Translation-Audit funktionieren.
- F: Benachrichtigungen sind dedupliziert und steuerbar.
- G: Performance, CI, Graphify und Release-Review sind abgeschlossen.

