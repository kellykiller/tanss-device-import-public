## TANSS Device Import 0.18.3

### TOTP als einziges Anmeldeverfahren

- Passkey/FIDO2 wurde vollständig aus dem Windows-Client und dem Importserver
  entfernt.
- Der Client zeigt nur noch die Eingabe des sechsstelligen TOTP-Codes an.
- Die vier früheren Passkey-Registrierungs- und Sitzungsendpunkte existieren
  nicht mehr.
- Die FIDO2-Abhängigkeiten, Passkey-Konfiguration, das Administrationsskript und
  das beschreibbare Credential-Volume wurden entfernt.
- `/status` meldet nur noch den TOTP-Status. Ohne verwendbares TOTP-Secret ist
  der Dienst nicht betriebsbereit.

### Upgrade von 0.18.2

`.env`, Secrets und TLS-Dateien werden übernommen. Die Variablen `PASSKEY_*` und
`IMPORT_API_DATA_PATH` sind nicht mehr erforderlich. Bestehende TOTP-Daten
bleiben unverändert.

Nach erfolgreichem Start und geprüftem Healthcheck von 0.18.3 können die alten
Passkey-Credentials vollständig gelöscht werden. Bei der bisherigen
Standardkonfiguration ist dies das Verzeichnis `/opt/tns-api/data/fido2`.
Der Updateablauf prüft den Pfad vor dem Löschen ausdrücklich; das TOTP-Secret
unter `/opt/tns-api/secrets/totp_secret` ist davon nicht betroffen.

Server und Windows-Client sollten gemeinsam auf 0.18.3 aktualisiert werden.
