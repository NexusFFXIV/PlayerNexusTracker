# Stufe 5 — Benachrichtigungen

## Arbeitspakete

- Regeln nach Spieler, History-Kategorie, Quelle und Erfolg/Fehler.
- Quiet Hours in lokaler Zeit.
- Digest-Modi: sofort, Zeitfenster oder Session-Ende.
- Stable Event-ID zur Deduplizierung.
- Verlauf mit erzeugt, unterdrückt, zusammengefasst und gesendet.
- Testbutton je Kategorie.
- Bestehende `NexusKit.ChatNotifications`-Abstraktion weiterverwenden.

## Tests und Akzeptanz

Quiet Hours, Digest, doppelte Events, fehlende Chat-Verbindung, Sprachwechsel
und deaktivierte Kategorien müssen funktionieren. Es darf keine
Notification-Spam-Schleife entstehen.

