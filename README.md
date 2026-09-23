# TANSS Device Import

TANSS Device Import erfasst Hardware- und Systemdaten eines Windows-Rechners und
überträgt ausgewählte Werte kontrolliert über einen separaten Importdienst an
TANSS. Der aktuelle Stand ist **0.18.2**.

## Komponenten

- `src/TanssSystemCapture.Client`: WPF-Client für Windows (`win-x64`, .NET 10)
- `server/app`: ASP.NET-Core-Importdienst für Linux/Docker (.NET 10)
- `server/bin`: Administrationsskripte für Passkeys, TOTP, Tokens und TLS
- `server/tests`: Regressionstests für Validierung und Übertragungslogik

## Bedienung

Beim Programmstart wird die HTTPS-Adresse des Importdienstes eingegeben. Sie ist
nicht in der EXE hinterlegt und wird nicht gespeichert. Anschließend erfolgt die
Anmeldung wahlweise mit:

- einem registrierten **Passkey (FIDO2-Hardware-Schlüssel)** durch Berührung oder
- einem sechsstelligen TOTP-Code.

Nach Kundenwahl und Datenerfassung kann das System direkt übertragen werden.
Der Client erzeugt dabei unsichtbar eine serverseitige Vorschau, bestätigt deren
SHA-256-Prüfsumme beim Schreiben und verhindert so eine veränderte Nutzlast. Die
vollständige Feldzuordnung und JSON-Nutzlast ist optional in einem eingeklappten
Bereich am unteren Fensterrand verfügbar.

Blockiert der Server eine Übertragung, wird der Bereich automatisch geöffnet,
der konkrete Grund angezeigt und das betroffene Eingabefeld hervorgehoben.

## Funktionsumfang

- Kundenauflösung und Hersteller-/Betriebssystemkataloge aus TANSS
- Neuanlage sowie Ergänzen oder Überschreiben vorhandener Geräte
- sichere Behandlung aktiver Dubletten und historischer inaktiver Vorgänger
- Systemdaten, Netzwerkadapter, VM-Host, TeamViewer-ID und Garantiedaten
- Wortmann-Seriennummernsuche einschließlich Hersteller-Artikelnummer
- optionale SAP-Business-One-Auflösung Seriennummer → TANSS-Artikelnummer
- Passkey- und TOTP-Sitzungen mit Rate-Limits
- read-only Container, Non-Root-Betrieb und externe Secret-Dateien

## Konfiguration

Das Repository enthält keine organisationsspezifischen URLs, IP-Adressen,
Datenbanknamen, Zertifikat-Fingerabdrücke oder Zugangsdaten. Eine Vorlage liegt
unter `server/.env.example`. Vor dem Start:

```bash
cd server
cp .env.example .env
# .env an die eigene Umgebung anpassen
docker compose config
docker compose up -d --build
```

Geheimnisse werden als Dateien unter dem in `.env` konfigurierten Secret-Pfad
bereitgestellt und niemals in `.env` oder Git gespeichert.

## Dokumentation

- [Technische Dokumentation](docs/TECHNISCHE_DOKUMENTATION.md)
- [Release Notes 0.18.2](docs/RELEASE_NOTES_0.18.2.md)
- [Serverbetrieb](server/README.md)

## Öffentliche Veröffentlichung

Vor einer öffentlichen Veröffentlichung muss neben dem aktuellen Dateibaum auch
die vollständige Git-Historie geprüft werden. Enthielt ein bisher privates
Repository interne Angaben, sollte ein neues öffentliches Repository mit frischer
Historie angelegt oder die alte Historie vollständig bereinigt werden.
