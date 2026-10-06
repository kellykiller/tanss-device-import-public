# Bedienung und Datenverarbeitung

## Voraussetzungen und Anmeldung

Benötigt werden Windows x64, Netzwerkzugriff auf den separat betriebenen Importdienst und dessen gültiger TOTP. Der Client ist für einen Importdienst mit dem Protokollstand 1.0.1 vorgesehen. Ein älterer Dienst kann erforderliche Funktionen wie die getrennte Zielerkennung nicht bereitstellen.

Beim Start sind Serveradresse und TOTP leer. Nach erfolgreicher Anmeldung werden der Kunde und die Exportdaten im Hauptfenster gewählt. Nach Ablauf einer Sitzung oder einem Neustart des Importdienstes ist eine neue Anmeldung erforderlich.

## Erfassung und Übertragung

Die automatisch erkannten Systemdaten lassen sich prüfen und bearbeiten. SAP-, Wortmann- und Rechnungsabfragen erfolgen über den angemeldeten Importdienst; die jeweiligen Schaltflächen starten einen erneuten Abruf.

Das Modell muss ausgefüllt sein. Optionale Felder werden nur entsprechend der Auswahl übertragen. Die Seriennummer beziehungsweise der Hostname dienen zusätzlich der Zielerkennung, selbst wenn deren Übertragung abgewählt ist.

Beim endgültigen Übertragen entscheidet der bestätigte Zielstatus:

- Kein vorhandenes Gerät: neues System anlegen.
- Gerät beim ausgewählten Kunden: ergänzen, ausgewählte Werte überschreiben oder abbrechen.
- Gerät bei einem anderen Kunden: blockieren und Zuordnung anzeigen.

Beim Ergänzen bleiben bereits vorhandene Werte bestehen. Beim Überschreiben gilt die aktuelle Feldauswahl. Die Übertragung wird durch eine serverseitig berechnete Vorschau-Prüfsumme an die geprüften Werte gebunden.

Bei einer virtuellen Maschine muss eine gültige Host-ID angegeben werden. DHCP und statische IPv4 schließen sich in der Adapterauswahl aus; jeder Adapter besitzt eine editierbare Bemerkung. Die Serverkennzeichnung ist eine einzelne Checkbox.

## Rechnungen

Der Importdienst meldet, ob er zur Seriennummer eine passende Rechnung gefunden hat. Eine manuell gewählte PDF hat für die Vorschau Vorrang vor der Mail-Rechnung, auch bevor ein Kunde gewählt ist. Manuelle PDFs sind auf 20 MiB begrenzt und werden auf die PDF-Kennung geprüft.

Eine manuelle Datei wird erst nach gültiger Kunden-/Seriennummernzuordnung beim Dienst vorgemerkt. Die endgültige TANSS-Ablage erfolgt nach einer bestätigten Geräteübertragung durch die separat betriebene Verarbeitung. Der Client zeigt den Verarbeitungsstatus an.

## Welche Daten liegen wo?

| Daten | Behandlung im Client |
| --- | --- |
| Serveradresse | Eingabe bei jedem Start; nur für die laufende Sitzung verwendet |
| Eingegebener TOTP-Code | Zur Anmeldung gesendet; Eingabefeld anschließend geleert; keine Einstellungsdatei |
| Sitzungstoken | Vom Importdienst erhalten; während der Sitzung im Arbeitsspeicher verwendet |
| TANSS-/Graph-/SAP-Secrets und TOTP-Seeds | Nicht Bestandteil des Clients; werden durch die private Betriebsumgebung verwaltet |
| Erfasste Gerätedaten und Kundeninformationen | Während der Nutzung im Speicher und in der Oberfläche; ausgewählte Werte werden an den Dienst gesendet |
| PDF-Vorschauen | Temporäre Kopien im Windows-Tempverzeichnis; Löschung beim regulären Schließen wird versucht |

Nach einem Absturz oder bei einer durch den PDF-Viewer gesperrten Datei können temporäre Vorschauen zurückbleiben. Laufzeitdaten, PDFs, Screenshots und Kopien produktiver Antworten dürfen nicht in öffentliche Commits oder Issues übernommen werden.

Der Client prüft HTTPS-Serverzertifikate mit dem Windows-Vertrauensspeicher und aktiviert die Zertifikatssperrprüfung. Zugriff und Schreibberechtigungen müssen durch den Importdienst durchgesetzt werden; das öffentliche Client-Repository enthält keinen dauerhaften Zugriffsschlüssel.
