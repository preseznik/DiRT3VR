import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import bloom  # noqa: E402
import glare  # noqa: E402
import seat_cameras  # noqa: E402
from seat_cameras import Node  # noqa: E402

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")
TEXT = "\r\n\t\t\t{}\r\n\t\t"


def param(name: str, number: str) -> Node:
    return Node("Param", TEXT.format(number), [("name", name)], [])


def sample() -> bytes:
    """Aspen-like streaks and flares, a blackout effect with flares already off, and bloom."""
    aspen = Node("Effect", None, [("name", "t~usa~aspen")], [
        Node("ParameterGroup", None, [("name", "Streaks")], [
            param("multiplier_0", "1.267200"), param("threshold_0", "0.752400"), param("multiplier_1", "1.158300"),
            param("multiplier_2", "3.999600"), param("upperLimit", "0.227700")]),
        Node("ParameterGroup", None, [("name", "LensFlares")], [
            param("multiplierAndThreshold_H0", "1.000000, 0.000000, 0.000000"), param("threshold", "0.100000"),
            param("multiplier", "2.000000")]),
        Node("ParameterGroup", None, [("name", "BloomFromDsX4")], [param("mixHuge", "1.999800")])])
    blackout = Node("Effect", None, [("name", "IG_blackout")], [
        Node("ParameterGroup", None, [("name", "Streaks")], [param("upperLimit", "0.000000")]),
        Node("ParameterGroup", None, [("name", "LensFlares")], [param("multiplier", "0.000000")])])
    root = Node("PostProcessEffects", None, [], [aspen, blackout])
    return seat_cameras.encode(seat_cameras.from_tree(root, [""]))


class GlareTests(unittest.TestCase):
    def test_lists_only_the_multipliers(self):
        self.assertEqual(glare.listing(sample()), [
            ("t~usa~aspen", "Streaks", "multiplier_0", 1.2672), ("t~usa~aspen", "Streaks", "multiplier_1", 1.1583),
            ("t~usa~aspen", "Streaks", "multiplier_2", 3.9996), ("t~usa~aspen", "LensFlares", "multiplier", 2.0),
            ("IG_blackout", "LensFlares", "multiplier", 0.0)])

    def test_disable_changes_only_the_multipliers(self):
        data = sample()
        changed = glare.disable_glare(data)
        self.assertTrue(all(number == 0 for *_, number in glare.listing(changed)))
        before, after = seat_cameras.decode(data), seat_cameras.decode(changed)
        targets = {element for *_, element in glare.multipliers(before)}
        for index, (old, new) in enumerate(zip(before.elements, after.elements)):
            if index not in targets:
                self.assertEqual(old, new)
        tree = seat_cameras.to_tree(after)
        streaks, flares, bloom_group = tree.children[0].children
        self.assertIn("0.752400", streaks.children[1].value)  # threshold kept
        self.assertIn("0.227700", streaks.children[4].value)  # upperLimit kept
        self.assertIn("1.000000, 0.000000", flares.children[0].value)  # per-element flare vector kept
        self.assertEqual(flares.children[2].value, TEXT.format("0.000000"))
        self.assertIn("1.999800", bloom_group.children[0].value)  # bloom untouched
        self.assertEqual(glare.disable_glare(changed), changed)

    def test_bloom_and_glare_compose(self):
        data = sample()
        both = glare.disable_glare(bloom.disable_bloom(data))
        self.assertEqual(both, bloom.disable_bloom(glare.disable_glare(data)))
        self.assertTrue(all(number == 0 for *_, number in glare.listing(both)))
        self.assertTrue(all(number == 0 for *_, number in bloom.listing(both)))

    def test_refuses_files_without_multipliers(self):
        root = Node("PostProcessEffects", None, [], [Node("Effect", None, [("name", "x")], [])])
        with self.assertRaises(ValueError):
            glare.disable_glare(seat_cameras.encode(seat_cameras.from_tree(root, [""])))

    @unittest.skipUnless((GAME / "postprocess/effects.xml").exists(), "DiRT 3 not installed")
    def test_installed_file(self):
        data = (GAME / "postprocess/effects.xml").read_bytes()
        rows = glare.listing(data)
        self.assertEqual(sum(1 for _, group, name, n in rows if group == "Streaks" and n), 150)  # 50 effects x 3
        self.assertEqual(sum(1 for _, group, _, n in rows if group == "LensFlares" and n), 55)
        changed = glare.disable_glare(data)
        self.assertTrue(all(number == 0 for *_, number in glare.listing(changed)))
        self.assertEqual(len(glare.listing(changed)), len(rows))
        self.assertEqual(bloom.listing(changed), bloom.listing(data))
        self.assertEqual(glare.disable_glare(bloom.disable_bloom(data)), bloom.disable_bloom(glare.disable_glare(data)))


if __name__ == "__main__":
    unittest.main()
