# TANSS Device Import API 0.18.3

Der Importdienst vermittelt ausschließlich zwischen dem Windows-Client und den
konfigurierten TANSS-/SAP-Schnittstellen. Tokens, Kennwörter und TLS-Schlüssel
werden aus externen Dateien gelesen. Die Anmeldung erfolgt ausschließlich mit
TOTP; Passkey/FIDO2 ist seit 0.18.3 vollständig entfernt.

## Voraussetzungen

- Linux mit Docker Engine und Docker Compose v2
- ein DNS-Name und ein gültiges TLS-Serverzertifikat
- TANSS-ERP- und Device-Management-Token
- ein eingerichtetes TOTP-Secret
- optional ein ausschließlich lesender SQL-Benutzer für SAP Business One

## Konfiguration

```bash
cp .env.example .env
chmod 600 .env
```

Mindestens anzupassen sind:

| Variable | Bedeutung |
|---|---|
| `TANSS_BASE_URL` | HTTPS-Basisadresse der TANSS-API |
| `TANSS_DEVICE_WEB_URL_TEMPLATE` | optionaler Weblink mit `{deviceId}` |
| `IMPORT_API_BIND_ADDRESS` | lokale/LAN-Adresse für den HTTPS-Port |
| `IMPORT_API_SECRETS_PATH` | Hostverzeichnis der Secret-Dateien |

## Secret-Dateien

```text
erp_token
device_management_token
totp_secret
sap_sql_password            # nur wenn SAP aktiviert ist
tls/fullchain.pem
tls/privkey.pem
```

Dateien müssen für den Containerbenutzer lesbar sein. Sie dürfen weder ins
Repository noch in das Server-ZIP aufgenommen werden.

## TOTP

```bash
sudo server/bin/manage-totp status
sudo server/bin/manage-totp rotate
```

TOTP verwendet RFC 6238, ein Zeitfenster von ±1 Schritt, Replay-Schutz,
Rate-Limit und nur gehashte Sitzungstokens im Arbeitsspeicher.

## SAP-Option

SAP ist standardmäßig deaktiviert. Für die Aktivierung werden `SAP_ENABLED`,
`SAP_SERVER`, `SAP_DATABASE` und `SAP_USER_NAME` in `.env` gesetzt sowie das
SQL-Kennwort als `sap_sql_password` bereitgestellt. Der SQL-Benutzer sollte
ausschließlich die für die Seriennummernsuche benötigten Leserechte besitzen.

## Start und Prüfung

```bash
docker compose config
docker compose build
docker compose up -d
curl --fail --silent http://127.0.0.1:8080/health
```

`/health` ist ein Liveness-Endpunkt und prüft weder TANSS noch SAP. Der mit TOTP
authentifizierte `/status`-Endpunkt zeigt Token- und TOTP-Status ohne Secrets.

## Aktualisierung von 0.18.2

Vor dem Wechsel `.env`, Secrets und TLS-Dateien übernehmen. Die früheren
Passkey-Variablen in `.env` werden nicht mehr ausgewertet und können entfernt
werden. Das alte Passkey-Datenverzeichnis wird nicht mehr in den Container
eingebunden.

```bash
unzip -tq tns-api-server-v0.18.3.zip
unzip -q tns-api-server-v0.18.3.zip -d tanss-device-import-v0.18.3
cd tanss-device-import-v0.18.3
cp /path/to/existing/.env .env
docker compose config
docker compose build
docker compose up -d
curl --fail --silent http://127.0.0.1:8080/health
```

Die Antwort muss `"version":"0.18.3"` enthalten. Erst anschließend die alten
Passkey-Credentials im früheren Datenverzeichnis löschen und den Client 0.18.3
verteilen.
