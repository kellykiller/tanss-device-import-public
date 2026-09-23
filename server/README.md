# TANSS Device Import API 0.18.0

Der Importdienst vermittelt ausschließlich zwischen dem Windows-Client und den
konfigurierten TANSS-/SAP-Schnittstellen. Tokens, Kennwörter und TLS-Schlüssel
werden aus externen Dateien gelesen.

## Voraussetzungen

- Linux mit Docker Engine und Docker Compose v2
- ein DNS-Name und ein gültiges TLS-Serverzertifikat für den Importdienst
- TANSS-ERP- und Device-Management-Token mit den benötigten Berechtigungen
- optional ein ausschließlich lesender SQL-Benutzer für SAP Business One
- mindestens ein registrierter Passkey oder ein eingerichtetes TOTP-Secret

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
| `PASSKEY_RP_ID` | DNS-Name des Importdienstes ohne Schema und Port |
| `PASSKEY_ORIGIN` | HTTPS-Origin des nativen WebAuthn-Clients |
| `IMPORT_API_BIND_ADDRESS` | lokale/LAN-Adresse für den HTTPS-Port |
| `IMPORT_API_SECRETS_PATH` | Hostverzeichnis der Secret-Dateien |
| `IMPORT_API_DATA_PATH` | persistentes Hostverzeichnis der Passkey-Daten |

Die Beispielwerte unter `example.org` sind Platzhalter und nicht für den
Produktivbetrieb bestimmt.

## Secret-Dateien

Im konfigurierten Secret-Verzeichnis erwartet der Container:

```text
erp_token
device_management_token
totp_secret                 # nur wenn TOTP verwendet wird
sap_sql_password            # nur wenn SAP aktiviert ist
tls/fullchain.pem
tls/privkey.pem
```

Dateien müssen für den Containerbenutzer lesbar sein. Sie dürfen weder ins
Repository noch in das Server-ZIP aufgenommen werden.

## Passkeys

Das Datenverzeichnis wird vorbereitet und ein einmaliger Registrierungscode
erzeugt mit:

```bash
sudo server/bin/manage-security-key prepare
sudo server/bin/manage-security-key enrollment-code 10
```

Im Client die Serveradresse eingeben, **Neuen Passkey registrieren …** wählen,
Bezeichnung und Code eingeben und den gewünschten FIDO2-Hardware-Schlüssel
berühren. Öffentliche Credential-Daten und Signaturzähler werden im persistenten
Datenverzeichnis gespeichert; der private Schlüssel verbleibt auf dem Gerät.

Weitere Befehle:

```bash
sudo server/bin/manage-security-key status
sudo server/bin/manage-security-key list
sudo server/bin/manage-security-key revoke CREDENTIAL-ID
```

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
SQL-Kennwort als `sap_sql_password` bereitgestellt. Der Server akzeptiert nur
die ausdrücklich konfigurierte Datenbank und den ausdrücklich konfigurierten
Benutzernamen. Der SQL-Benutzer sollte ausschließlich `SELECT` auf der für die
Seriennummernsuche benötigten Tabelle erhalten.

`SAP_TRUST_SERVER_CERTIFICATE=false` sollte beibehalten werden. Die ausstellende
CA des SQL-Serverzertifikats muss im Container vertrauenswürdig sein.

## Start und Prüfung

```bash
docker compose config
docker compose build
docker compose up -d
curl --fail --silent http://127.0.0.1:8080/health
```

`/health` ist ein Liveness-Endpunkt und prüft weder TANSS noch SAP. Der
authentifizierte `/status`-Endpunkt zeigt Token-, Passkey- und TOTP-Status, ohne
Secret-Inhalte zurückzugeben.

## Aktualisierung

Vor dem Wechsel auf 0.18.0 müssen `.env`, Secrets, TLS-Dateien und das persistente
Passkey-Datenverzeichnis gesichert bzw. weiterverwendet werden. Danach:

```bash
unzip -tq tns-api-server-v0.18.0.zip
unzip -q tns-api-server-v0.18.0.zip -d tanss-device-import-v0.18.0
cd tanss-device-import-v0.18.0
cp /path/to/existing/.env .env
docker compose config
docker compose build
docker compose up -d
curl --fail --silent http://127.0.0.1:8080/health
```

Die Antwort muss `"version":"0.18.0"` enthalten. Anschließend `/status` über
die authentifizierte HTTPS-Schnittstelle prüfen und erst danach den Client
0.18.0 verteilen.
