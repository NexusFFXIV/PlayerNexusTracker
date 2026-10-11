# Agenten-, Skill- und Review-Prozess

## Reihenfolge

1. Graphify prüft Modulgrenzen und Abhängigkeiten.
2. Spec-/Plan-Review prüft Verhalten und Akzeptanzkriterien.
3. TDD ergänzt Regressionstests vor der Implementierung.
4. Incremental Implementation liefert Domain, Persistence, UI und Notifications
   als getrennte vertikale Scheiben.
5. Observability prüft strukturierte, nicht-sensitive Diagnoseereignisse.
6. Code Review prüft Architektur, Sicherheit, Performance und Komplexität.

## Parallelisierung

Agents dürfen unabhängige Analysen und Tests parallel bearbeiten. Gemeinsame
Persistence- und API-Modelle werden erst nach dem Status-/Retry-Checkpoint von
einem verantwortlichen Branch zusammengeführt.

## Abschlussprüfung

Vollständige Tests, Release-Build, CI, Graphify-Update, Datenschutzprüfung,
Localization-Fallbackprüfung und Review der Änderungsdokumentation.

