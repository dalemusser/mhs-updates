#!/usr/bin/env python3
"""Assign GardenBoxesUIController._placeCameraButton in TerasGardenBoxesUI.prefab.

Inserts `_placeCameraButton: {fileID: <Place button>}` into the controller's MonoBehaviour
block (fileID 5593944335515394450), right after the `buttons:` list, which is where Unity
serializes the field (declaration order). Idempotent.
"""
import sys

PREFAB = sys.argv[1]
CONTROLLER_ID = "&5593944335515394450"
PLACE_BUTTON_ID = "1276088063958510367"   # Button on TerasGardenBoxesUI/TeraGardenBoxCanvas/Static UI/HUD/PlaceCamera
LINE = f"  _placeCameraButton: {{fileID: {PLACE_BUTTON_ID}}}\n"

lines = open(PREFAB, encoding="utf-8").read().split("\n")
lines = [l + "\n" for l in lines[:-1]] + ([lines[-1]] if lines[-1] else [])
start = next(i for i, l in enumerate(lines) if l.startswith("--- !u!114 ") and l.rstrip().endswith(CONTROLLER_ID))
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("--- !u!"))
block = lines[start:end]
if any(l.startswith("  _placeCameraButton:") for l in block):
    print("already assigned")
    sys.exit(0)
buttons_at = next(i for i in range(start, end) if lines[i] == "  buttons:\n")
after = buttons_at + 1
while after < end and lines[after].startswith("  - "):
    after += 1
assert lines[after] == "  plantUsables:\n", lines[after]
lines.insert(after, LINE)
open(PREFAB, "w", encoding="utf-8", newline="").write("".join(lines))
print(f"inserted at line {after + 1}: {LINE.strip()}")
