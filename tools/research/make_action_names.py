"""Builds tools/data/action-names.json from DiRT 3's stock action maps (lenient parse)."""
import collections
import json
import re
from pathlib import Path

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition\actionmap")
DRIVING = ["Accelerate", "Brake", "Steer Left", "Steer Right", "Handbrake", "Clutch", "Gear Up", "Gear Down",
           "Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Gear Reverse", "Horn", "Change View",
           "Look Back", "Look Left", "Look Right", "Look Up", "Look Down", "Pause"]

listed = set(re.findall(r'filename="actionmap/([^"]+)"', (GAME / "actionMapPaths.xml").read_text(encoding="utf-8")))
maps = {}
uses = collections.Counter()
for path in sorted(GAME.glob("*.xml")):
    if path.name == "actionMapPaths.xml":
        continue
    text = path.read_text(encoding="utf-8", errors="replace")
    header = re.search(r'<ActionMap\s+actionMapName="([^"]*)"\s+deviceName="([^"]*)"\s+priority="([^"]*)"', text)
    actions = sorted(set(re.findall(r'<Action\s+actionName="([^"]+)"', text)))
    for action in actions:
        uses[action] += 1
    maps[path.name] = {"actionMapName": header.group(1) if header else None,
                       "deviceName": header.group(2) if header else None,
                       "priority": int(header.group(3)) if header and header.group(3).isdigit() else None,
                       "actions": len(actions),
                       "loaded": path.name in listed}

missing = [name for name in DRIVING if name not in uses]
assert not missing, missing
document = {
    "source": "DiRT 3 Complete Edition (Steam) actionmap/*.xml, read leniently (two stock files are not well-formed XML); "
              "loaded = listed in actionMapPaths.xml (the game reads only those)",
    "actions": [{"name": name, "driving": name in DRIVING, "maps": uses[name]} for name in sorted(uses)],
    "maps": maps,
}
out = Path("tools/data/action-names.json")
out.write_text(json.dumps(document, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
print(len(uses), "actions,", sum(a["driving"] for a in document["actions"]), "driving,", len(maps), "maps ->", out)
