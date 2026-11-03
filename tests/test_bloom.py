import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import bloom  # noqa: E402
import seat_cameras  # noqa: E402
from seat_cameras import Node  # noqa: E402

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")
TEXT = "\r\n\t\t\t{}\r\n\t\t"


def param(name: str, number: str) -> Node:
    return Node("Param", TEXT.format(number), [("name", name)], [])


def sample() -> bytes:
    """A track effect with bloom on, race_base with bloom mixes already zero, and a ColourBalance group."""
    track = Node("Effect", None, [("name", "t~finland~finland_rally")], [
        Node("ParameterGroup", None, [("name", "BloomFromDsX4")], [
            param("lowerThreshold", "0.156100"), param("mixMedium", "0.217800"), param("mixLarge", "0.217800"),
            param("mixHuge", "1.999800")]),
        Node("ParameterGroup", None, [("name", "ColourBalance")], [param("bloomPrevalence", "0.500000")])])
    race = Node("Effect", None, [("name", "race_base")], [
        Node("ParameterGroup", None, [("name", "BloomFromDsX4")], [param("mixMedium", "0.000000"), param("mixStreak", "0.066500")])])
    root = Node("PostProcessEffects", None, [], [track, race])
    return seat_cameras.encode(seat_cameras.from_tree(root, [""]))


TEXT_SAMPLE = ('<?xml version="1.0" encoding="UTF-8" ?>\r\n<PostProcessEffects>\r\n'
               '\t<Effect name="Effect_0001" id="2" chain="Type6">\r\n'
               '\t\t<ParameterGroup name="BloomFromDsX4">\r\n'
               '\t\t\t<Param name="lowerThreshold">\r\n\t\t\t\t3.000000\r\n\t\t\t</Param>\r\n'
               '\t\t\t<Param name="mixMedium">\r\n\t\t\t\t0.069400\r\n\t\t\t</Param>\r\n'
               '\t\t\t<Param name="mixLarge">\r\n\t\t\t\t0.000000\r\n\t\t\t</Param>\r\n'
               '\t\t</ParameterGroup>\r\n'
               '\t\t<ParameterGroup name="ToneMap">\r\n'
               '\t\t\t<Param name="mixMedium">\r\n\t\t\t\t0.500000\r\n\t\t\t</Param>\r\n'
               '\t\t</ParameterGroup>\r\n\t</Effect>\r\n</PostProcessEffects>\r\n').encode()


class BloomTests(unittest.TestCase):
    def test_lists_only_bloom_mixes(self):
        self.assertEqual(bloom.listing(sample()), [("t~finland~finland_rally", "mixMedium", 0.2178), ("t~finland~finland_rally", "mixLarge", 0.2178),
                                                   ("t~finland~finland_rally", "mixHuge", 1.9998), ("race_base", "mixMedium", 0.0),
                                                   ("race_base", "mixStreak", 0.0665)])

    def test_binary_disable_changes_only_the_mixes(self):
        data = sample()
        changed = bloom.disable_bloom(data)
        self.assertTrue(all(number == 0 for _, _, number in bloom.listing(changed)))
        before, after = seat_cameras.decode(data), seat_cameras.decode(changed)
        self.assertEqual(len(before.elements), len(after.elements))
        mixes = {element for _, _, element in bloom.mixes(before)}
        for index, (old, new) in enumerate(zip(before.elements, after.elements)):
            if index in mixes:
                self.assertEqual(old[0], new[0])
            else:
                self.assertEqual(old, new)
        # Whitespace around each changed value is kept.
        _, _, element = bloom.mixes(after)[2]
        self.assertEqual(after.string(after.elements[element][1]), TEXT.format("0.000000"))
        # The threshold and ColourBalance's bloomPrevalence keep their strings.
        tree = seat_cameras.to_tree(after)
        self.assertIn("0.156100", tree.children[0].children[0].children[0].value)
        self.assertIn("0.500000", tree.children[0].children[1].children[0].value)
        self.assertEqual(bloom.disable_bloom(changed), changed)

    def test_text_disable_changes_only_the_mix_numbers(self):
        changed = bloom.disable_bloom(TEXT_SAMPLE)
        expected = TEXT_SAMPLE.replace(b"\t\t\t\t0.069400\r\n", b"\t\t\t\t0.000000\r\n")
        self.assertEqual(changed, expected)
        self.assertEqual(bloom.listing(changed), [("Effect_0001", "mixMedium", 0.0), ("Effect_0001", "mixLarge", 0.0)])
        self.assertEqual(bloom.disable_bloom(changed), changed)

    def test_refuses_files_without_bloom(self):
        root = Node("PostProcessEffects", None, [], [Node("Effect", None, [("name", "x")], [])])
        with self.assertRaises(ValueError):
            bloom.disable_bloom(seat_cameras.encode(seat_cameras.from_tree(root, [""])))
        with self.assertRaises(ValueError):
            bloom.disable_bloom(b"<PostProcessEffects></PostProcessEffects>")

    @unittest.skipUnless((GAME / "postprocess/effects.xml").exists(), "DiRT 3 not installed")
    def test_installed_files(self):
        for rel, active in (("postprocess/effects.xml", 60),("tracks/generic/effects.xml", 1), ("tracks/generic/night_effects.xml", 1)):
            data = (GAME / rel).read_bytes()
            rows = bloom.listing(data)
            self.assertEqual(len({effect for effect, _, number in rows if number}), active, rel)
            changed = bloom.disable_bloom(data)
            self.assertTrue(all(number == 0 for _, _, number in bloom.listing(changed)), rel)
            self.assertEqual(len(bloom.listing(changed)), len(rows), rel)
            if data[:4] == bloom.BINARY:
                self.assertEqual(seat_cameras.encode(seat_cameras.decode(data)), data, rel)


if __name__ == "__main__":
    unittest.main()
