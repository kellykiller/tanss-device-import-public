"""Check the public client boundary without printing potential secret values."""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {".gitignore", "AGENTS.md", "CONTRIBUTING.md", "Directory.Build.props", "LICENSE", "README.md", "SECURITY.md", "global.json"}
TOOLS = {"check_public_repository.py", "check_dependency_audit.py", "package_client.py", "test_report.py"}
WORKFLOWS = {"ci.yml", "release.yml"}
bootstrap = "--bootstrap" in sys.argv[1:]
if bootstrap:
    TOOLS.add("bootstrap_reset.py")
    WORKFLOWS.add("bootstrap-client-only.yml")

result = subprocess.run(["git", "-C", str(ROOT), "ls-files", "-z"], capture_output=True, check=True)
paths = [Path(name) for name in result.stdout.decode().split("\0") if name]
findings = []
patterns = {
    "private key": r"-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",
    "GitHub credential": r"gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}",
    "AWS credential": r"AKIA[A-Z0-9]{16}",
    "OTP provisioning URI": r"otpauth:[/][/]",
    "JWT literal": r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
    "credential assignment": r'(?i)(?:password|secret|apiKey|apiToken|connectionString)\s*(?:=|:)\s*"[^"\r\n]+"',
}
internal = re.compile(r"(?i)helps\.(?:de|local)|\bHLP\d+[-_][A-Z0-9_-]+|\bR(?:7219007|8110927)\b|\b10\.0\.\d+\.\d+\b")
for relative in paths:
    parts = relative.parts
    allowed = (relative.as_posix() in ROOT_FILES or
               parts[:2] == ("src", "TanssSystemCapture.Client") or
               parts[:2] == ("tests", "ClientSmoke") or
               (len(parts) == 2 and parts[0] == "docs" and relative.suffix == ".md") or
               (len(parts) == 2 and parts[0] == "tools" and parts[1] in TOOLS) or
               (len(parts) == 3 and parts[:2] == (".github", "workflows") and parts[2] in WORKFLOWS))
    forbidden = (any(part in ("bin", "obj", "TestResults", "secrets", "server", "worker") for part in parts) or
                 relative.suffix.lower() in (".pdf", ".zip", ".exe", ".dll", ".pfx", ".p12", ".pem", ".key", ".sqlite", ".db") or
                 relative.name.startswith(".env"))
    if not allowed or forbidden:
        findings.append((str(relative), "outside public client boundary"))
        continue
    path = ROOT / relative
    if path.is_symlink():
        findings.append((str(relative), "symlink"))
        continue
    if relative.suffix == ".ico":
        if relative.as_posix() != "src/TanssSystemCapture.Client/Assets/TanssSystemCapture.ico":
            findings.append((str(relative), "unreviewed binary"))
        continue
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeError, OSError):
        findings.append((str(relative), "unreadable text"))
        continue
    if internal.search(text):
        findings.append((str(relative), "internal example data"))
    for description, pattern in patterns.items():
        if re.search(pattern, text):
            findings.append((str(relative), description))
if findings:
    for path, reason in findings:
        print(f"{path}: {reason}")
    raise SystemExit("Public client repository check failed")
print(f"Public client boundary and credential-pattern check: OK ({len(paths)} files)")
