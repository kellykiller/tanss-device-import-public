## TANSS Device Import 0.18.0

Funktionsrelease für einen konfigurierbaren und öffentlich veröffentlichbaren
Client-/Server-Stand.

## Neuerungen

- Die Adresse des Importdienstes ist nicht mehr fest in der Windows-EXE
  hinterlegt. Sie wird bei jedem Programmstart eingegeben und nur für die
  laufende Sitzung verwendet.
- Der primäre Ablauf überträgt Systeme direkt nach einer verständlichen
  Bestätigung. Die sicherheitsrelevante serverseitige Vorschau mit
  SHA-256-Prüfsumme bleibt intern erhalten.
- Feldzuordnung und geplante JSON-Nutzlast stehen optional in einem standardmäßig
  eingeklappten Bereich am unteren Fensterrand zur Verfügung.
- Blockierungsgründe werden vollständig angezeigt; zugehörige Eingabefelder
  werden farblich hervorgehoben.
- Die Anmeldung wurde auf **Passkey (FIDO2-Hardware-Schlüssel)** und TOTP
  vereinheitlicht. Markenbezogene Bezeichnungen wurden entfernt.
- Clientzertifikat-/mTLS-Anmeldung und der fest hinterlegte
  Zertifikat-Fingerabdruck wurden entfernt.
- Alle Endpunkte außer der lokalen Liveness-Prüfung erzwingen HTTPS jetzt auch
  direkt im Anwendungscode.
- Der Link zu einem geschriebenen TANSS-System ist jetzt optional serverseitig
  konfigurierbar und nicht mehr im Client fest hinterlegt.
- Compose, Skripte, Namespaces und Dokumentation enthalten keine konkrete
  Organisationsinfrastruktur mehr. Beispielwerte verwenden ausschließlich
  reservierte Beispieldomains.
- SAP-Datenbank und SQL-Benutzer werden über die Betriebsumgebung freigegeben;
  produktspezifische Namen sind nicht im Quellcode festgelegt.

## Sicherheit

- Ausschließlich HTTPS-Importserveradressen werden akzeptiert.
- Benutzerinformationen, Query-Strings und Fragmente sind in der eingegebenen
  Basisadresse unzulässig.
- Das Schreib-API verlangt weiterhin exakt die unmittelbar zuvor serverseitig
  berechnete Vorschau-Prüfsumme.
- Passkeys sind auf externe FIDO2-Authentifikatoren und Berührungsbestätigung
  ausgelegt; TOTP bleibt als alternative Anmeldung erhalten.
- Secrets und private Schlüssel bleiben außerhalb des Repositorys.

## Aktualisierung

Version 0.18.0 ändert **Server und Windows-Client**. Zuerst Serverkonfiguration
und Server aktualisieren, anschließend `/health` und `/status` prüfen und danach
den Client verteilen. Bestehende FIDO2-Credentials bleiben erhalten, wenn das
konfigurierte Datenverzeichnis übernommen wird. Die frühere mTLS-Anmeldung steht
nicht mehr zur Verfügung.

## Pakete

- `tns-api-server-v0.18.0.zip`
- `TanssSystemCapture.Client-v0.18.0-win-x64.zip`
- `SHA256SUMS.txt`
