# Beiträge

Dieses Repository ist auf den Windows-Client beschränkt. Verwende .NET 10, erhalte den portablen Einzel-EXE-Build und führe die Client-Tests unter Windows aus.

Für Tests ausschließlich erfundene Daten und reservierte Domains wie `example.org` oder `test.invalid` verwenden. Mock-TOTP und Mock-Sitzungstoken sind nur in lokalen HTTP-Stubs zulässig. Reale Serveradressen, Hostnamen, Kunden, Seriennummern, PDFs, API-Antworten und Secrets bleiben außerhalb öffentlicher Änderungen.

Vor einem Pull Request:

1. `python tools/check_public_repository.py` ausführen.
2. Client bauen und `dotnet test tests/ClientSmoke/ClientSmoke.csproj --configuration Release` ausführen.
3. Änderungen an Bedienung oder Vertrag in der passenden Client-Dokumentation festhalten.

Server- und Workeränderungen werden separat mit dem betreibenden Team abgestimmt. Fehlerberichte enthalten die Clientversion, reproduzierbare Schritte und anonymisierte Angaben; bei Sicherheitsproblemen [SECURITY.md](SECURITY.md) beachten.
