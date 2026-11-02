import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import motion_blur  # noqa: E402
import seat_cameras  # noqa: E402
from seat_cameras import Node  # noqa: E402

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")
TEXT = "\r\n\t\t\t{}\r\n\t\t"


def param(name: str, number: str) -> Node:
    return Node("Param", TEXT.format(number), [("name", name)], [])


def sample() -> bytes:
    """race_base with MotionBlur and Dof groups; a track effect without motion blur; a flashback effect."""
    race = Node("Effect", None, [("name", "race_base")], [
        Node("ParameterGroup", None, [("name", "Dof")], [param("blurAmount", "0.871200")]),
        Node("ParameterGroup", None, [("name", "MotionBlur")], [
            param("partitionSwitch", "1.000000"), param("blurLength", "6.000000"), param("frozenBlurLength", "0.250000")])])
    track = Node("Effect", None, [("name", "t~losangeles~coliseum")], [
        Node("ParameterGroup", None, [("name", "Lighting")], [param("intensity", "0.000000")])])
    flashback = Node("Effect", None, [("name", "FlashbackFX_01")], [
        Node("ParameterGroup", None, [("name", "MotionBlur")], [param("blurLength", "7.028900")])])
    root = Node("PostProcessEffects", None, [], [race, track, flashback])
    return seat_cameras.encode(seat_cameras.from_tree(root, [""]))


class MotionBlurTests(unittest.TestCase):
    def test_lists_only_motion_blur_lengths(self):
        document = seat_cameras.decode(sample())
        found = [(effect, name, motion_blur.value(document, element)) for effect, name, element in motion_blur.lengths(document)]
        self.assertEqual(found, [("race_base", "blurLength", 6.0), ("race_base", "frozenBlurLength", 0.25),
                                 ("FlashbackFX_01", "blurLength", 7.0289)])

    def test_disable_changes_only_the_lengths(self):
        data = sample()
        original = seat_cameras.decode(data)
        changed = seat_cameras.decode(motion_blur.disable_motion_blur(data))
        self.assertEqual([motion_blur.value(changed, e) for _, _, e in motion_blur.lengths(changed)], [0, 0, 0])
        self.assertEqual(changed.strings[:len(original.strings)], original.strings)  # no existing string edited
        self.assertEqual(changed.attributes, original.attributes)
        moved = [i for i, (a, b) in enumerate(zip(original.elements, changed.elements)) if a != b]
        self.assertEqual([original.name(i) for i in moved], ["Param"] * 3)
        # Whitespace around each value is kept; partitionSwitch and Dof are untouched.
        lengths = {name: changed.string(changed.elements[e][1]) for effect, name, e in motion_blur.lengths(changed) if effect == "race_base"}
        self.assertEqual(lengths["blurLength"], TEXT.format("0.000000"))

    def test_idempotent_and_roundtrip(self):
        data = sample()
        once = motion_blur.disable_motion_blur(data)
        self.assertEqual(motion_blur.disable_motion_blur(once), once)
        self.assertEqual(seat_cameras.encode(seat_cameras.decode(data)), data)

    def test_refusals(self):
        no_blur = seat_cameras.encode(seat_cameras.from_tree(Node("PostProcessEffects", None, [], [
            Node("Effect", None, [("name", "plain")], [])]), [""]))
        with self.assertRaises(ValueError):
            motion_blur.disable_motion_blur(no_blur)
        cameras = seat_cameras.encode(seat_cameras.from_tree(Node("VehicleViewDefinition", None, [], []), [""]))
        with self.assertRaises(ValueError):
            motion_blur.disable_motion_blur(cameras)
        bad = seat_cameras.encode(seat_cameras.from_tree(Node("PostProcessEffects", None, [], [
            Node("Effect", None, [("name", "x")], [Node("ParameterGroup", None, [("name", "MotionBlur")], [
                Node("Param", "fast", [("name", "blurLength")], [])])])]), [""]))
        with self.assertRaises(ValueError):
            motion_blur.disable_motion_blur(bad)

    @unittest.skipUnless((GAME / "postprocess" / "effects.xml").is_file(), "DiRT 3 is not installed")
    def test_installed_effects_file(self):
        data = (GAME / "postprocess" / "effects.xml").read_bytes()
        self.assertEqual(seat_cameras.encode(seat_cameras.decode(data)), data)
        original = seat_cameras.decode(data)
        found = motion_blur.lengths(original)
        self.assertIn(("race_base", "blurLength", 6.0),
                      [(effect, name, motion_blur.value(original, e)) for effect, name, e in found])
        changed = seat_cameras.decode(motion_blur.disable_motion_blur(data))
        self.assertTrue(all(motion_blur.value(changed, e) == 0 for _, _, e in motion_blur.lengths(changed)))
        moved = {i for i, (a, b) in enumerate(zip(original.elements, changed.elements)) if a != b}
        self.assertEqual(moved, {e for _, _, e in found if motion_blur.value(original, e) != 0})
        self.assertEqual(changed.attributes, original.attributes)


if __name__ == "__main__":
    unittest.main()
