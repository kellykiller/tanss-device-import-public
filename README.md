# TANSS Systemerfassung – Windows-Client

Öffentlicher Quellcode des portablen Windows-Clients **1.0.1**. Der Client erfasst Systemdaten und überträgt ausgewählte Angaben über einen separat betriebenen Importdienst nach TANSS.

Dieses Repository enthält ausschließlich den Client, neutrale Tests, Dokumentation und Client-Builds. Importserver, Rechnungsworker, Serverinstallationspakete und Betriebsdaten werden separat und privat verwaltet.

## Download und Start

Die [Releases](https://github.com/kellykiller/tanss-device-import-public/releases) enthalten eine einzelne `TanssSystemCapture.Client-v1.0.1.exe`, dieselbe EXE als ZIP und `SHA256SUMS.txt`.

1. Die EXE herunterladen und unter Windows x64 starten. Eine separate .NET-Installation ist nicht erforderlich; der Client verlangt keine Administratorrechte.
2. Die HTTPS-Adresse des vorhandenen Importdienstes eingeben, beispielsweise `https://import.example.org`.
3. Einen aktuellen sechsstelligen **Importdienst-TOTP** eingeben. Die Startfelder sind leer; ein TANSS-Passwort wird im Client nicht abgefragt.
4. Nach der Anmeldung den Kunden auswählen. Die Kundenauswahl kann im Hauptfenster geändert werden.

Die EXE ist bewusst unsigniert. SHA256-Prüfsummen werden zu jeder Veröffentlichung erzeugt. Der Betrieb setzt einen kompatiblen, separat eingerichteten Importdienst voraus; die EXE allein stellt keinen TANSS-Dienst bereit.

## Funktionen

- Lokale Erkennung von Hostname, Modell, Hersteller, Betriebssystem, Seriennummer, TeamViewer-ID und aktiven IPv4-Netzwerkadaptern.
- Automatische SAP-, Wortmann-Modell-, Garantie- und Rechnungsabfragen über den Importdienst; Schaltflächen für erneute Abfragen bleiben verfügbar.
- Dauerhafter Dark Mode, Kundenwechsel und editierbare Exportfelder.
- Modell als Pflichtfeld; optionale Angaben lassen sich einzeln abwählen.
- Vorhandene Geräte beim ausgewählten Kunden ergänzen oder ausgewählte Werte überschreiben. Geräte eines anderen Kunden werden durch den Importdienst gesperrt.
- Virtuelle Maschine mit verpflichtender Host-ID; einheitliche Servercheckbox.
- DHCP beziehungsweise statische IPv4 sowie freie Adapterbemerkungen.
- Gefundene Rechnungs-PDF anzeigen oder ein eigenes PDF auswählen. Die gewählte Datei wird erst im Zusammenhang mit einer bestätigten Geräteübertragung zur Ablage vorgemerkt.
- Optionale Übertragungsvorschau und Status der nachgelagerten PDF-Verarbeitung.

## Dokumentation

- [Bedienung und Datenverarbeitung](docs/CLIENT.md)
- [Build, Tests und Releases](docs/BUILD.md)
- [Client-Release 1.0.1](docs/RELEASE_NOTES_1.0.1.md)
- [Beitragsregeln](CONTRIBUTING.md)
- [Sicherheitsmeldungen](SECURITY.md)

## Entwicklung

Windows x64 und .NET SDK 10 sind für Build und WPF-Tests erforderlich. Der Client wird direkt aus `src/TanssSystemCapture.Client/TanssSystemCapture.Client.csproj` gebaut. Der Build benötigt keine Serverquellen und keine produktiven Zugangsdaten.

```powershell
dotnet restore tests/ClientSmoke/ClientSmoke.csproj
dotnet test tests/ClientSmoke/ClientSmoke.csproj --configuration Release
dotnet publish src/TanssSystemCapture.Client/TanssSystemCapture.Client.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/client
```

Die öffentliche Historie beginnt mit dem bereinigten Client-Stand 1.0.1. Frühere gemeinsame Client-/Server-Releases wurden aus der aktiven Veröffentlichung entfernt. Alte lokale Klone sollten neu geklont werden, damit die vorherige Historie nicht erneut eingespielt wird.

## Lizenz

[MIT](LICENSE).
