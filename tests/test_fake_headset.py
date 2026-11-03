import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from fake_headset import null_headset, parse_setting, restore, user_root, with_settings

# The shape SteamVR writes: three-space indents, other sections kept.
ORIGINAL = (b'{\n   "dashboard" : {\n      "enableDashboard" : true\n   },\n'
            b'   "steamvr" : {\n      "mirrorViewGeometry" : "0 0 1080 600",\n      "forcedDriver" : "lighthouse"\n   }\n}\n')


class NullHeadsetTests(unittest.TestCase):
    def test_switches_to_the_null_driver_and_keeps_other_settings(self):
        settings = json.loads(null_headset(ORIGINAL, 2016, 2240))
        self.assertEqual(settings["steamvr"]["forcedDriver"], "null")
        self.assertTrue(settings["steamvr"]["activateMultipleDrivers"])
        self.assertEqual(settings["steamvr"]["mirrorViewGeometry"], "0 0 1080 600")
        self.assertEqual(settings["dashboard"], {"enableDashboard": True})
        self.assertEqual((settings["power"]["turnOffScreensTimeout"], settings["power"]["pauseCompositorOnStandby"]),
                         (86400.0, False))
        null = settings["driver_null"]
        self.assertEqual((null["enable"], null["renderWidth"], null["renderHeight"], null["displayFrequency"]),
                         (True, 2016, 2240, 90.0))

    def test_creates_missing_sections_and_refuses_odd_files(self):
        settings = json.loads(null_headset(b"{}", 3072, 3264))
        self.assertEqual((settings["steamvr"]["forcedDriver"], settings["driver_null"]["renderHeight"]), ("null", 3264))
        for data in (b"[]", b'{"steamvr": 3}', b'{"power": []}'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                null_headset(data)
        for size in ((639, 2240), (2016, 4097)):
            with self.subTest(size=size), self.assertRaises(ValueError):
                null_headset(ORIGINAL, *size)

    def test_launcher_settings_folder_matches_the_launcher(self):
        # PC4080S's settings folder for the Steam install (launcher/Core.vb: SHA-256 of the upper-case path).
        game = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")
        self.assertEqual(user_root(game, Path("L")).name, "6DA447A4B18C68CE5BD77103")
        self.assertEqual(user_root(Path(str(game) + "\\"), Path("L")), user_root(game, Path("L")))

    def test_run_settings_change_only_vr_hud_and_perf_with_their_types(self):
        saved = b'{"VrRenderScale": 100, "VrHelmetView": false, "LaunchMode": "direct"}'
        changed = json.loads(with_settings(saved, dict([parse_setting("VrFieldOfView=85"), parse_setting("VrHelmetView=true"),
                                                         parse_setting("VrAntiAliasing=2xmsaa")])))
        self.assertEqual(changed, {"VrRenderScale": 100, "VrHelmetView": True, "LaunchMode": "direct",
                                   "VrFieldOfView": 85, "VrAntiAliasing": "2xmsaa"})
        for change in ({"LaunchMode": "normal"}, {"VrRenderScale": "150"}, {"VrHelmetView": 1}, {"vr": 1}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                with_settings(saved, change)

    def test_restore_puts_back_the_exact_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "steamvr.vrsettings"
            target.write_bytes(null_headset(ORIGINAL))
            self.assertTrue(restore(target, ORIGINAL))
            self.assertEqual(target.read_bytes(), ORIGINAL)


if __name__ == "__main__":
    unittest.main()
