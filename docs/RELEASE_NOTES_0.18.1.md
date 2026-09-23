## TANSS Device Import 0.18.1

Wartungsrelease für Installationen mit vom Standard abweichenden Hostpfaden.

### Korrekturen

- `manage-security-key` liest `IMPORT_API_DATA_PATH` aus der lokalen `.env`.
- `manage-totp` und `replace-token` lesen `IMPORT_API_SECRETS_PATH` aus der
  lokalen `.env`.
- `deploy-certificate` verwendet standardmäßig das Verzeichnis der zugehörigen
  `compose.yaml` und berücksichtigt ebenfalls den konfigurierten Secret-Pfad.
- Containerneustarts der Administrationsskripte verwenden das tatsächlich
  installierte Compose-Projekt.
- `.env`-Werte werden gezielt und ohne Ausführung als Shellcode gelesen.
- Absolute Hostpfade werden vor Änderungen validiert.
- Automatisierte Shelltests prüfen Standardwerte, `.env`-Werte und ausdrückliche
  Umgebungsüberschreibungen.

### Kompatibilität

Serverkonfiguration, Secrets, TOTP und bereits registrierte Passkeys bleiben
unverändert. Der Windows-Client enthält gegenüber 0.18.0 keine funktionale
Änderung und wird lediglich auf denselben Patchstand versioniert.

### Aktualisierung

Zuerst das Serverpaket aktualisieren und dieselbe `.env` sowie dieselben
persistenten Secret- und Passkey-Verzeichnisse weiterverwenden. Danach
`/health` auf Version `0.18.1` prüfen. Der Client 0.18.0 bleibt protokollseitig
kompatibel; für einen einheitlichen Versionsstand steht zusätzlich Client
0.18.1 bereit.
