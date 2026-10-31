import json
from pathlib import Path
import re
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import extract_graphics_options as ego

CATALOG = Path(__file__).resolve().parents[1] / "tools" / "data" / "graphics-options.json"
# Writes stay inside the graphics quality elements; CPU, audio, display, stereo, input and physics never appear.
QUALITY_ELEMENTS = {"night_lighting", "shadows", "particles", "mirrors", "crowd", "cloth", "dynamic_ambient_occ",
                    "groundcover", "vehicles", "trees", "objects", "envmap", "water", "postprocess", "skidmarks"}


def options_fixture():
    refs = "".join(f'<ref category="{name}" option="low" />' for _, name, _ in ego.QUALITY if name not in ("shadows", "post"))
    default = ' type="default"'
    presets = "".join(
        f'<option name="{preset}"{default if preset == "medium" else ""}>'
        f'<graphics_detail level="{preset}" />{refs}'
        '<ref category="shadows" option="low" dx_11_option="ultra" /><ref category="post" option="low" />'
        '<ref category="multisampling" option="2xmsaa" /><ref category="physics" option="high" /></option>'
        for preset in ego.PRESETS)
    simple = "".join(
        f'<category name="{name}"><option name="low"><{name} enabled="true" /></option></category>'
        for _, name, _ in ego.QUALITY if name != "shadows")
    return ('<hardware_config_options><category name="graphicsDetail">' + presets + '</category>' + simple +
            '<category name="shadows"><option name="low"><shadows enabled="true" size="768" /></option>'
            '<option name="ultra" dx11Only="true"><shadows enabled="true" size="2048" /></option></category>'
            '</hardware_config_options>').encode()


class GraphicsCatalogTests(unittest.TestCase):
    def setUp(self):
        self.catalog = json.loads(CATALOG.read_text(encoding="utf-8"))

    def test_committed_catalog_is_complete_and_consistent(self):
        self.assertEqual(self.catalog["schema"], 1)
        self.assertRegex(self.catalog["sourceOptionsSha256"], r"^[0-9a-f]{64}$")
        fields = [category["field"] for category in self.catalog["categories"]]
        self.assertEqual(fields, [field for field, _, _ in ego.QUALITY])
        choices = {}
        for category in self.catalog["categories"]:
            ids = [choice["id"] for choice in category["choices"]]
            self.assertEqual(len(ids), len(set(ids)), category["field"])
            self.assertIn(category["default"], ids)
            choices[category["field"]] = set(ids)
            for choice in category["choices"]:
                self.assertTrue(choice["attributes"], category["field"])
                for target, value in choice["attributes"].items():
                    element, attribute = target.split("@")
                    self.assertIn(element, QUALITY_ELEMENTS)
                    self.assertRegex(attribute, r"^[A-Za-z_]+$")
                    self.assertRegex(value, r"^[A-Za-z0-9.]{1,16}$")
        self.assertEqual([preset["id"] for preset in self.catalog["presets"]], ego.PRESETS)
        self.assertEqual([preset["id"] for preset in self.catalog["presets"] if preset.get("gameDefault")], ["medium"])
        for preset in self.catalog["presets"]:
            self.assertEqual(set(preset["values"]), set(fields))
            for field, value in {**preset["values"], **preset.get("dx9Differences", {})}.items():
                self.assertIn(value, choices[field], preset["id"] + " " + field)

    def test_defaults_are_the_game_default_preset(self):
        medium = next(preset for preset in self.catalog["presets"] if preset["id"] == "medium")
        self.assertEqual({category["field"]: category["default"] for category in self.catalog["categories"]},
                         medium["values"])

    def test_dx11_options_are_used_and_dx9_differences_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "hardware_settings_options.xml"
            path.write_bytes(options_fixture())
            catalog = ego.build(path)
        shadows = next(category for category in catalog["categories"] if category["field"] == "Shadows")
        self.assertEqual(shadows["choices"][1], {"id": "ultra", "label": "Ultra", "dx11Only": True,
                                                 "attributes": {"shadows@enabled": "true", "shadows@size": "2048"}})
        for preset in catalog["presets"]:
            self.assertEqual(preset["values"]["Shadows"], "ultra")
            self.assertEqual(preset["dx9Differences"], {"Shadows": "low"})
            self.assertEqual(preset["other"], {"multisampling": "2xmsaa", "physics": "high"})
        self.assertEqual(shadows["default"], "ultra")

    @unittest.skipUnless((ego.DEFAULT_GAME / "system" / "hardware_settings_options.xml").is_file(),
                         "DiRT 3 is not installed in the default Steam folder")
    def test_catalog_matches_installed_game(self):
        text = ego.render(ego.build(ego.DEFAULT_GAME / "system" / "hardware_settings_options.xml"))
        self.assertEqual(text, CATALOG.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
