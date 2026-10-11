# Stufe 3 — Localization

## Sprachmodi

`Auto`, `Spiel-/Dalamud-Sprache`, `English` und `Deutsch` werden als persistierte
Einstellung angeboten.

## Arbeitspakete

- Laufzeitwechsel ohne Neustart.
- Effektiven Sprachmodus anzeigen.
- Englischen Fallback vereinheitlichen.
- Missing-Key- und Fallback-Prüfung in Development/CI.
- Zentrale Datums-/Zeitformat-Helfer.
- Plural-Helfer nur bei realem Bedarf.
- Alle neuen Status-, Run- und Notification-Texte über `ILocalizer`.

## Akzeptanz

Kein Neustart ist nötig; fehlende Schlüssel werden in Development und CI
erkannt; ECommons-Lokalisierung wird nicht eingeführt.

