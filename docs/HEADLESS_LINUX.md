# Linux-Headless-Client

Der Headless-Client erfasst ein Linux-System ohne grafische Oberfläche und
überträgt die ausgewählten Daten über denselben Importdienst wie der
Windows-Client. Die erste unterstützte Zielplattform ist Linux x64 mit Ubuntu
oder Debian.

## Sicherheitsmodell

- Anmeldung ausschließlich mit einem interaktiv und verdeckt eingegebenen
  TOTP-Code
- keine Übergabe von TOTP-Codes als Parameter, Umgebungsvariable oder Pipe
- reguläre TLS-Prüfung über den Vertrauensspeicher des Linux-Systems
- serverseitige Vorschau vor jedem möglichen Schreibzugriff
- Schreiben erst nach der exakten Eingabe `UEBERTRAGEN`
- garantiert reiner Vorschauablauf mit `--preview-only`

## Erfasste Daten

- Hostname
- DMI-Hersteller, Produktname und Seriennummer aus `/sys/class/dmi/id`
- Betriebssystem aus `/etc/os-release`
- wahrscheinlicher VM-Status anhand der DMI-Daten
- geeignete aktive IPv4-Netzwerkadapter mit MAC-Adresse und Standardroute
- DHCP-Erkennung bei systemd-networkd; bei unbekanntem DHCP-Status wird keine
  vermeintlich statische IPv4-Adresse übertragen

Nicht lesbare Informationen blockieren die Erfassung nicht. Das
TANSS-Pflichtfeld Modell wird bei Bedarf interaktiv abgefragt.

## Veröffentlichung

```bash
dotnet publish \
  src/TanssSystemCapture.Headless/TanssSystemCapture.Headless.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output artifacts/headless
```

Das Ergebnis ist die einzelne ausführbare Datei `tanss-system-capture`.

## Verwendung

```bash
chmod +x tanss-system-capture

./tanss-system-capture \
  --api-url https://import.example.org:45001/ \
  --customer 10047 \
  --server
```

Nur Vorschau:

```bash
./tanss-system-capture \
  --api-url https://import.example.org:45001/ \
  --customer 10047 \
  --preview-only
```

Die Importdienst-Adresse kann alternativ für die aktuelle Shell gesetzt
werden:

```bash
export TANSS_IMPORT_API_URL=https://import.example.org:45001/
./tanss-system-capture --customer 10047
```

Alle Optionen zeigt `./tanss-system-capture --help`.

## Grenzen der ersten Version

- kein FIDO2-/Passkey-Login; auf Headless-Systemen wird TOTP verwendet
- keine automatische TeamViewer-ID-Erkennung
- keine vollautomatische oder zeitgesteuerte Übertragung
- zunächst ein vorausgewählter, wahrscheinlich relevanter Netzwerkadapter
- vor der produktiven Freigabe Tests auf den eingesetzten Ubuntu-/Debian-Versionen
