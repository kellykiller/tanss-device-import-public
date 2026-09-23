## TANSS Device Import 0.18.2

### Behobene Fehler

- Die Übertragungsvorschau und Geräteübertragung funktionieren nun auch dann,
  wenn der TANSS-Kundendetailabruf per interner Firmen-ID kein `displayId`
  liefert. Die ursprüngliche Kundenauswahl bleibt weiterhin durch die exakte
  Kundennummer und die interne TANSS-ID abgesichert.
- Der Windows-Client verwendet wieder die zum WebAuthn-Adapter passende
  `Fido2.Models`-Version 4.0.1. Dadurch wird der Laufzeitfehler
  `Attempted to access a missing method` beim Registrieren oder Verwenden eines
  FIDO2-Hardware-Schlüssels vermieden.
- Clientanzeigen verwenden nach Vorschau und Übertragung den bereits eindeutig
  geprüften Kunden, auch wenn TANSS im späteren Detailabruf keine Kundennummer
  zurückgibt.

### Sicherheit und Validierung

- `displayId` bleibt bei der Kundensuche nach Kundennummer ein Pflichtfeld und
  muss weiterhin exakt der eingegebenen Kundennummer entsprechen.
- Interne Firmen-ID und Kundenname bleiben beim Detailabruf zwingend; eine von
  TANSS abweichend zurückgegebene Firmen-ID wird weiterhin abgelehnt.
- Regressionstests decken Detailantworten ohne `displayId` sowie die weiterhin
  strenge Kundensuche ab.

### Aktualisierung

Server und Windows-Client sollten gemeinsam auf 0.18.2 aktualisiert werden.
Persistente Secrets, TOTP-Konfiguration und bereits registrierte Passkeys werden
bei unveränderten Datenpfaden weiterverwendet.
