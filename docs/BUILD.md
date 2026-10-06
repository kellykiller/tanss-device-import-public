# Client bauen, testen und veröffentlichen

## Werkzeugstand

- Windows x64 für WPF-Build und Tests.
- .NET SDK 10; `global.json` erlaubt aktuelle Feature-Versionen innerhalb von .NET 10.
- Python 3.13 oder neuer für Repository-Prüfung und Paketwerkzeuge.
- Kein Importserver, Docker, reales TANSS-Konto oder produktives TOTP-Secret wird für den Build benötigt.

## Lokale Prüfung

Im Repository-Verzeichnis:

```powershell
python tools/check_public_repository.py
dotnet restore tests/ClientSmoke/ClientSmoke.csproj
dotnet build src/TanssSystemCapture.Client/TanssSystemCapture.Client.csproj --configuration Release
dotnet test tests/ClientSmoke/ClientSmoke.csproj --configuration Release --logger trx --collect:"XPlat Code Coverage"
```

Die Tests verwenden lokale HTTP-Stubs, reservierte Beispieldomains und erfundene Kunden-/Gerätedaten. Die WPF-Regression prüft leere Anmeldung, vorhandene Felder, Dark Mode, VM-Hostpflicht, Rechnungs-Priorität, optionale Exportfelder und Kundensperre. Eine echte Serveranmeldung wird nicht ausgeführt.

## Portable EXE

```powershell
dotnet publish src/TanssSystemCapture.Client/TanssSystemCapture.Client.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/client
python tools/package_client.py artifacts/client release-assets
```

Der Publish-Ordner muss genau eine `TanssSystemCapture.Client.exe` enthalten. Das Paketwerkzeug verlangt eine Windows-EXE und erzeugt die versionierte EXE, ein ZIP mit exakt dieser Datei und SHA256-Prüfsummen. Es nimmt keine Einstellungsdateien, PDFs, Serverpakete oder zusätzlichen Build-Dateien auf.

## GitHub Actions

`ci.yml` prüft öffentliche Dateien, baut den Client, prüft NuGet-Abhängigkeiten und führt die Client-Tests aus. Es läuft bei Änderungen auf `main`, Pull Requests und manuellem Start. CI veröffentlicht keine Pakete und besitzt nur Leserechte.

`release.yml` führt dieselben Prüfungen aus und veröffentlicht ausschließlich Client-Dateien. Ein Release kann über einen Tag `vX.Y.Z` oder manuell gestartet werden. Bei einem Tag muss die Tagversion exakt zur Projektversion passen. Ein bestehendes Release wird nicht überschrieben. Für den manuellen Start muss `main` gewählt werden.

Für einen neuen Release:

1. Projektversion und `app.manifest` anpassen; Fenstertitel und Releasehinweise aktualisieren.
2. Tests und CI prüfen.
3. Die neue Version auf `main` übernehmen.
4. Den passenden Versionstag erstellen oder den Releaseworkflow auf `main` manuell starten.

Der Releaseworkflow verwendet den kurzlebigen Repository-Token von GitHub Actions. Produktive TANSS-, Graph-, SAP- oder Azure-Secrets werden weder benötigt noch in diesem Repository konfiguriert. Authenticode-Signierung ist für den internen Einsatz keine Release-Voraussetzung; die EXE wird unsigniert veröffentlicht.

## Trennung vom Server

Dieses Repository ist die öffentliche Client-Basis. Serverquellen, Worker und Deployment werden getrennt privat verwaltet. Änderungen am API-Vertrag müssen mit dem betreibenden Team abgestimmt werden; private Betriebsunterlagen und echte Daten bleiben außerhalb dieses Repositorys.
