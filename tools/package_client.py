"""Package only the portable client EXE and verify its ZIP and checksums."""
import hashlib
import re
import shutil
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source, target = map(Path, sys.argv[1:])
version = ET.parse(root / "src/TanssSystemCapture.Client/TanssSystemCapture.Client.csproj").getroot().findtext("PropertyGroup/Version")
if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
    raise SystemExit("Invalid client version")
items = list(source.iterdir())
if len(items) != 1 or items[0].name != "TanssSystemCapture.Client.exe" or not items[0].is_file():
    raise SystemExit("Publish directory must contain exactly the client EXE")
with items[0].open("rb") as executable:
    if executable.read(2) != b"MZ":
        raise SystemExit("Publish output is not a Windows executable")
target.mkdir(parents=True, exist_ok=True)
name = f"TanssSystemCapture.Client-v{version}.exe"
exe = target / name
archive = target / f"TanssSystemCapture.Client-v{version}-win-x64.zip"
if any(target.iterdir()):
    raise SystemExit("Release output directory must be empty")
shutil.copyfile(items[0], exe)
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as output:
    output.write(exe, arcname=name)
with zipfile.ZipFile(archive) as output:
    if output.namelist() != [name] or output.testzip() is not None:
        raise SystemExit("Client archive verification failed")
    zipped = hashlib.sha256(output.read(name)).hexdigest()
def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()
if digest(exe) != zipped:
    raise SystemExit("Packaged EXE differs from published EXE")
manifest = "".join(f"{digest(path)}  {path.name}\n" for path in (exe, archive))
(target / "SHA256SUMS.txt").write_text(manifest, encoding="utf-8", newline="\n")
print(manifest, end="")
