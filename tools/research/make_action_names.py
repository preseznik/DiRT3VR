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


def bindings(path: Path) -> list[dict]:
    """Every (action, input, calibrator) of one map, in file order."""
    text = path.read_text(encoding="utf-8", errors="replace")
    result = []
    for action in re.finditer(r'<Action\s+actionName="([^"]+)"\s*>(.*?)</Action>', text, re.S):
        for axis in re.finditer(r'<Axis\s+axisName="([^"]+)"\s*>(.*?)</Axis>', action.group(2), re.S):
            calibrator = re.search(r"<CalibratorSimple\s+([^/>]*)/?>", axis.group(2))
            attributes = dict(re.findall(r'(\w+)="([^"]*)"', calibrator.group(1))) if calibrator else {}
            entry = {"action": action.group(1), "axisName": axis.group(1), "type": attributes.get("type")}
            for key in ("deadZone", "saturation"):
                if key in attributes:
                    entry[key] = float(attributes[key])
            result.append(entry)
    return result


axis_names = set()
for path in sorted(GAME.glob("*.xml")):
    if path.name != "actionMapPaths.xml":
        axis_names.update(re.findall(r'axisName="([^"]+)"', path.read_text(encoding="utf-8", errors="replace")))
buttons = sorted(int(n.rsplit("_", 1)[1]) for n in axis_names if re.fullmatch(r"di_button_\d+", n))
stock = {
    "source": document["source"],
    "notes": {"di_button": f"zero-based: di_button_{buttons[0]} .. di_button_{buttons[-1]} in the stock maps",
              "vk_code": "Windows virtual-key code, e.g. vk_code_0x26 = Up arrow",
              "defaults": "a CalibratorSimple without deadZone/saturation uses the map's stated defaults "
                          "(0.2 / 0.8, threshold 0.5, per the map comments)"},
    "maps": {name: bindings(GAME / name) for name in ("wm_keyboard.xml", "xinput_pad.xml", "dinput_default.xml")},
    "axisNames": sorted(axis_names),
}
stock_out = Path("tools/data/stock-bindings.json")
stock_out.write_text(json.dumps(stock, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
print({name: len(entries) for name, entries in stock["maps"].items()}, len(axis_names), "axis names ->", stock_out)
