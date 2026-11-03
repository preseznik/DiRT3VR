import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from fake_headset import null_headset, restore

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

    def test_restore_puts_back_the_exact_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "steamvr.vrsettings"
            target.write_bytes(null_headset(ORIGINAL))
            self.assertTrue(restore(target, ORIGINAL))
            self.assertEqual(target.read_bytes(), ORIGINAL)


if __name__ == "__main__":
    unittest.main()
