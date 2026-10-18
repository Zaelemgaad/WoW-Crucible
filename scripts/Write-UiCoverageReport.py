"""Catalog declared callbacks and publish the observed UI coverage without guessing links.

Run after --ui-coverage. The source catalog is separate from runtime cases: one
factory callback can produce many controls, so a label match is not coverage.
"""
import csv
import json
import re
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "docs/validation/ui-coverage"
OUTPUT.mkdir(parents=True, exist_ok=True)
sources = subprocess.check_output(["git", "ls-files", "src/WoWCrucible.Desktop", "src/WoWCrucible.ClassStudio"], cwd=ROOT, text=True).splitlines()
events = r"Click|SelectionChanged|IsCheckedChanged|ValueChanged|TextChanged|KeyDown|KeyUp|LostFocus|GotFocus|PointerPressed|PointerReleased|PointerMoved|PointerWheelChanged|PointerCaptureLost|PointerEntered|PointerExited|DoubleTapped|Tapped|Expanded|Collapsed|Drop|DragOver|DragEnter|DragLeave"
patterns = {
    ".cs": re.compile(r"[\w.]+\.((?:" + events + r"))\s*\+=|AddHandler\([^;\n]+"),
    ".axaml": re.compile(r"(?:" + events + r")=\"[^\"]+\""),
    ".js": re.compile(r"addEventListener\(\s*['\"][^'\"]+['\"]|\.on(?:click|change|input|keydown|pointerdown|pointermove|pointerup)\s*="),
    ".html": re.compile(r"\bon(?:click|change|input|keydown|pointerdown|pointermove|pointerup)=\"[^\"]+\""),
}
declarations = []
for relative in sources:
    path = ROOT / relative
    pattern = patterns.get(path.suffix)
    if pattern is None:
        continue
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        for match in pattern.finditer(line):
            declarations.append({"source": relative, "line": number, "column": match.start() + 1,
                                 "hook": match.group(), "declaration": line.strip(),
                                 "coverage": "Source declaration only; see runtime cases. No inferred activation."})

def write_csv(name, rows):
    with (OUTPUT / name).open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]), lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)

write_csv("source-callbacks.csv", declarations)
report = json.loads((ROOT / ".local/ui-coverage/runtime.json").read_text(encoding="utf-8"))
rows = sorted(report["Cases"], key=lambda row: row["Id"])
write_csv("runtime-controls.csv", rows)
groups = {}
for row in rows:
    groups.setdefault(row["Scope"], Counter())[row["Status"]] += 1
summary = ["# UI activation coverage", "", f"Observed: {report['UpdatedUtc']}", "",
           "This is an activation inventory, not a claim that every workflow succeeds.",
           "`activated` means the recorded handler/selection was exercised. Picker cancellation,",
           "empty-data validation, and successful loaded-data actions are distinguished in Evidence.",
           "`untested` cases remain explicit; disabled controls are never force-enabled.", "",
           "| Screen | Activated | Failed | Untested |", "| --- | ---: | ---: | ---: |"]
for scope, counts in sorted(groups.items()):
    summary.append(f"| {scope} | {counts['activated']} | {counts['failed']} | {counts['untested']} |")
totals = Counter(row["Status"] for row in rows)
summary += ["", f"Total: {len(rows)} observed cases; {totals['activated']} activated, {totals['failed']} failed, {totals['untested']} untested.",
            f"Unhandled/runner errors: {len(report['Errors'])}.", "",
            "- [Every observed control/option and its evidence](runtime-controls.csv)",
            "- [Declared source callbacks](source-callbacks.csv)", "",
            f"Source catalog: {len(declarations)} detected callback declarations. It includes factories",
            "and handlers for contexts not instantiated by the runtime walk. It is a textual",
            "catalog, not a compiler proof: indirect bindings, templates, and data-generated",
            "controls can require additional scenarios. Source rows are never counted as activated",
            "merely because a runtime control has a similar label.", "",
            "Reproduce from the checkout root:", "", "```powershell",
            "dotnet run --project tests/WoWCrucible.Desktop.Tests -- --ui-coverage --activate",
            "python scripts/Write-UiCoverageReport.py", "```", ""]
summary += ["## Remaining coverage", "",
            "The counts include individual dropdown choices, repeated controls in different",
            "screens, and read-only outputs; they are not a count of unique app features.",
            "Native controls are driven through handlers/properties and browser controls through",
            "DOM events. This does not test OS mouse hit-testing, native file-dialog interaction,",
            "or every data-dependent state. Live SQL, deployment, Windows integration, and",
            "large asset workflows need separate isolated fixtures. No live client/server data",
            "was written by this run.", "",
            "| Untested reason | Cases |", "| --- | ---: |"]
for reason, count in Counter(row["Evidence"] for row in rows if row["Status"] == "untested").most_common():
    summary.append(f"| {reason.replace('|', '/').replace(chr(10), ' ')} | {count} |")
with (OUTPUT / "README.md").open("w", encoding="utf-8", newline="\n") as stream:
    stream.write("\n".join(summary))
print(f"Published {len(rows)} runtime cases and {len(declarations)} source declarations to {OUTPUT}")
