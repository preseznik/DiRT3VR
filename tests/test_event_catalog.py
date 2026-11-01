import json
from pathlib import Path
import re
import unittest

CATALOG = Path(__file__).resolve().parents[1] / "tools" / "data" / "event-catalog.json"
GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")


class EventCatalogTests(unittest.TestCase):
    def setUp(self):
        self.catalog = json.loads(CATALOG.read_text(encoding="utf-8"))

    def test_catalog_is_consistent(self):
        self.assertEqual(self.catalog["schema"], 1)
        self.assertRegex(self.catalog["sourceDatabaseSha256"], r"^[0-9a-f]{64}$")
        disciplines = {d["id"]: d for d in self.catalog["disciplines"]}
        self.assertEqual(set(disciplines), {"rally", "rallycross", "trailblazer", "landrush"})
        for discipline in disciplines.values():
            self.assertIn(discipline["trackType"], ("circuit", "point_to_point"))
            self.assertTrue(discipline["laps"] and all(1 <= lap <= 20 for lap in discipline["laps"]))
            if discipline["trackType"] == "point_to_point":
                self.assertEqual(discipline["laps"], [1])
        ids = [route["id"] for route in self.catalog["routes"]]
        self.assertEqual(len(ids), len(set(ids)))
        for route in self.catalog["routes"]:
            self.assertIn(route["discipline"], disciplines)
            for part in ("country", "track", "route"):
                self.assertRegex(route[part], r"^[a-z0-9_]+$")  # safe for the benchmark XML and paths
            self.assertTrue(route["name"] and route["trackName"] and route["locationName"])
        codes = [car["code"] for car in self.catalog["cars"]]
        self.assertEqual(len(codes), len(set(codes)))
        for car in self.catalog["cars"]:
            self.assertIn(car["discipline"], disciplines)
            self.assertRegex(car["code"], r"^[a-z0-9]{2,4}$")
            self.assertTrue(car["name"])
        for discipline in disciplines:
            self.assertTrue(any(r["discipline"] == discipline for r in self.catalog["routes"]), discipline)
            self.assertTrue(any(c["discipline"] == discipline for c in self.catalog["cars"]), discipline)

    def test_benchmark_proven_selection_is_present(self):
        self.assertTrue(any(r["country"] == "usa" and r["track"] == "aspen" and r["route"] == "route_0" for r in self.catalog["routes"]))
        self.assertIn("frx", {car["code"] for car in self.catalog["cars"]})

    @unittest.skipUnless((GAME / "tracks" / "locations").is_dir(), "DiRT 3 is not installed in the default Steam folder")
    def test_every_entry_is_installed(self):
        for route in self.catalog["routes"]:
            self.assertTrue((GAME / "tracks" / "locations" / route["country"] / route["track"] / route["route"]).is_dir(), route)
        for car in self.catalog["cars"]:
            self.assertTrue((GAME / "cars" / "models" / car["code"]).is_dir(), car)


if __name__ == "__main__":
    unittest.main()
