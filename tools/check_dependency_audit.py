"""Fail CI when NuGet reports vulnerable direct or transitive packages."""
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if report.get("problems"):
    raise SystemExit("Dependency audit could not complete")
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for key in ("topLevelPackages", "transitivePackages"):
            if any(package.get("vulnerabilities") for package in framework.get(key, [])):
                raise SystemExit("Vulnerable NuGet dependencies found; inspect dependency-audit.json")
print("NuGet vulnerability audit: OK")
