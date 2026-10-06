"""Summarize standard TRX and Cobertura reports without consuming artifact quota."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(sys.argv[1])
print("| Bericht | Ergebnis |\n| --- | --- |")
for path in sorted(root.rglob("*.trx")):
    document = ET.parse(path)
    counters = document.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
    if counters is not None:
        print(f"| Tests | {counters.get('passed')} bestanden / {counters.get('total')} gesamt; {counters.get('failed')} fehlgeschlagen |")
for path in sorted(root.rglob("coverage.cobertura.xml")):
    document = ET.parse(path).getroot()
    print(f"| Zeilenabdeckung | {float(document.get('line-rate', '0')) * 100:.1f}% ({document.get('lines-covered')}/{document.get('lines-valid')}) |")
