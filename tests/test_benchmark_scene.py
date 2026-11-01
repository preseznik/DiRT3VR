import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from run_benchmark import choose_scene  # noqa: E402


class BenchmarkSceneTests(unittest.TestCase):
    def test_default_is_the_proven_aspen_frx_scene(self):
        self.assertEqual(choose_scene(None, None, 0),
                         {"country": "usa", "track": "aspen", "route": "route_0", "discipline": "rallycross", "cars": ["frx"]})

    def test_player_first_then_distinct_opponents_of_the_route_discipline(self):
        scene = choose_scene(None, "frx", 3)
        self.assertEqual(scene["cars"][0], "frx")
        self.assertEqual(len(scene["cars"]), 4)
        self.assertEqual(len(set(scene["cars"])), 4)
        rally = choose_scene("finland/finland_rally/route_0", None, 2)
        self.assertEqual(rally["discipline"], "rally")
        self.assertEqual(len(rally["cars"]), 3)

    def test_unknown_or_unsafe_values_are_refused(self):
        for route, car, opponents in (("usa/aspen/route_99", None, 0), ("../x/y", None, 0), ("usa/aspen", None, 0),
                                      (None, "nope", 0), (None, "frx", 8), ("usa/aspen/route_6", None, 5)):
            with self.assertRaises(ValueError, msg=(route, car, opponents)):
                choose_scene(route, car, opponents)


if __name__ == "__main__":
    unittest.main()
