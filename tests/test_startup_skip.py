import sys
import unittest
from pathlib import Path
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "research"))
import seat_cameras  # noqa: E402
import startup_skip  # noqa: E402
from inspect_cameras import decode_xml  # noqa: E402
from seat_cameras import Node  # noqa: E402

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")


def sample() -> bytes:
    states = [Node("StateVideoPreStart", None, [("id", "sting_video"), ("filename", "video\\sting.bik"),
                                                ("filename_us", "video\\sting.bik"), ("skippable", "true")], []),
              Node("StateVideoPreStart", None, [("id", "amd_sting_video"), ("filename", "video\\AMD_sting.bik"),
                                                ("filename_us", "video\\AMD_sting.bik"), ("skippable", "true")], []),
              Node("ScreenInitialLegal", None, [("id", "screen_initial_legal"), ("glyph", "initial_legal")], []),
              Node("StateGarageZone", None, [("id", "press_start_cutscene"), ("usable_zones", "1"),
                                             ("camera_group", "press_start"), ("skip_rrc", "true")], []),
              Node("StatePostProcessTransition", None, [("id", "fade_from_white"), ("overlay", "off")], []),
              Node("StateWayPoint", None, [("id", "other"), ("timeDelay", "0.0")], [])]
    return seat_cameras.encode(seat_cameras.from_tree(Node("xml", None, [], states), [""]))


def expected(data: bytes, parts) -> bytes:
    """The independent decoder's view of the same edit."""
    tree = decode_xml(data)
    for part in parts:
        for tag, ident in startup_skip.NODES[part]:
            node = tree.find(f".//{tag}[@id='{ident}']")
            node.tag = "StateWayPoint"
            node.attrib.clear()
            node.attrib.update(id=ident, timeDelay="0.0")
    return ET.tostring(tree)


class StartupSkipTests(unittest.TestCase):
    def test_each_part_and_all_turn_only_their_nodes_into_waypoints(self):
        data = sample()
        for parts in (("movies",), ("legal",), ("cutscene",), ("movies", "legal", "cutscene")):
            with self.subTest(parts=parts):
                changed = startup_skip.skip_startup(data, parts)
                self.assertEqual(ET.tostring(decode_xml(changed)), expected(data, parts))
                self.assertEqual(len(changed), len(data))
                self.assertEqual(startup_skip.skip_startup(changed, parts), changed)

    def test_parts_compose_in_any_order(self):
        data = sample()
        self.assertEqual(startup_skip.skip_startup(startup_skip.skip_startup(data, ("movies",)), ("legal", "cutscene")),
                         startup_skip.skip_startup(data, ("movies", "legal", "cutscene")))

    def test_fade_and_other_states_stay(self):
        tree = decode_xml(startup_skip.skip_startup(sample(), ("movies", "legal", "cutscene")))
        self.assertIsNotNone(tree.find(".//StatePostProcessTransition[@id='fade_from_white'][@overlay='off']"))
        self.assertEqual(len(tree.findall(".//StateWayPoint")), 5)

    def test_refusals(self):
        with self.assertRaises(ValueError):
            startup_skip.skip_startup(sample(), ())
        with self.assertRaises(ValueError):
            startup_skip.skip_startup(sample(), ("press_start",))
        missing = seat_cameras.encode(seat_cameras.from_tree(Node("xml", None, [], [Node("StateWayPoint", None, [("id", "x")], [])]), [""]))
        with self.assertRaises(ValueError):
            startup_skip.skip_startup(missing, ("legal",))

    def test_research_run_journals_and_restores_states(self):
        import hashlib
        import tempfile
        from unittest.mock import patch
        import diagnostic_files as df
        data = sample()
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(df, "STATES_SHA256", hashlib.sha256(data).hexdigest()), \
                patch.object(df, "STARTUP_SKIP", ("legal", "cutscene")):
            root = Path(temporary)
            game = root / "game"
            (game / "system").mkdir(parents=True)
            (game / df.STATES_NAME).write_bytes(data)
            journal = root / "deployment.json"
            transaction = df.DiagnosticFiles(game, journal, "e" * 32)
            transaction.apply_replacement(df.STATES_NAME, data)
            self.assertEqual((game / df.STATES_NAME).read_bytes(), startup_skip.skip_startup(data, ("legal", "cutscene")))
            self.assertEqual(df.DiagnosticFiles.recover(journal, game), [])
            self.assertEqual((game / df.STATES_NAME).read_bytes(), data)
            with self.assertRaises(ValueError):
                df.startup_skip_states(data + b"\0")

    @unittest.skipUnless((GAME / "system/states.bin").exists(), "DiRT 3 not installed")
    def test_installed_file(self):
        data = (GAME / "system/states.bin").read_bytes()
        everything = startup_skip.skip_startup(data, ("movies", "legal", "cutscene"))
        self.assertEqual(ET.tostring(decode_xml(everything)), expected(data, ("movies", "legal", "cutscene")))
        self.assertEqual(sum(a != b for a, b in zip(data, everything)), 26)
        # The movies part is the launcher's existing edit (StartupMovies.vb), byte for byte.
        movies = startup_skip.skip_startup(data, ("movies",))
        self.assertEqual(sum(a != b for a, b in zip(data, movies)), 14)
        self.assertEqual(ET.tostring(decode_xml(movies)), expected(data, ("movies",)))


if __name__ == "__main__":
    unittest.main()
