# PlayerNexusTracker implementation checklist

Detailed scope, dependencies and acceptance criteria live in
[`plan.md`](plan.md). Work is executed one feature branch/PR per slice.

- [x] Phase 0: Baseline, Regressionstests und `.gitignore`
- [ ] Checkpoint A: Tests, Build und Architekturbericht grün
- [x] Phase 1: `RefreshOutcome`, Quellenstatus und Queue-Event
- [ ] Checkpoint B: Status-/Queue-Vertrag stabil
- [ ] Phase 2: Run-Logs, Migration, Retention und Diagnoseexport (Persistenz/API vorhanden; UI-Gateway offen)
- [ ] Checkpoint C: Run-Log/Export geprüft
- [ ] Phase 3: Sprachmodus, Laufzeitwechsel und Translation-Audit (Grundlage/Audit vorhanden)
- [ ] Checkpoint E: EN/DE vollständig und schaltbar
- [ ] Phase 4: Timeline und Snapshot-Diff (Diff-/Timeline-API vorhanden; UI offen)
- [ ] Checkpoint D: History-Migration und Diff geprüft
- [ ] Phase 5: Quiet Hours, Digest und Notification-History (Policy/Buffer/History vorhanden; UI/Flush offen)
- [ ] Checkpoint F: Deduplizierung geprüft
- [ ] Phase 6: Freshness, selektive Refreshes, Metriken und Domain-Tests (Metrik-Grundlage vorhanden)
- [ ] Phase 7: Changelog und Release-Gates
- [ ] Checkpoint G: CI-, Graphify- und Code-Review vollständig

