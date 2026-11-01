import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "research"))
import seat_cameras  # noqa: E402
from inspect_cameras import decode_xml  # noqa: E402

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")


def sample() -> bytes:
    """VehicleViewDefinition > ViewManager > [chase View, head-cam View > position Parameter]."""
    strings = ["VehicleViewDefinition", "ViewManager", "name", "frx", "View", "ident", "chase_close", "type",
               "Mounted", "hidden", "false", "head-cam", "Head", "Parameter", "position", "vector3", "x", "y", "z",
               "0.3460", "1.0680", "-0.3170"]
    s = {value: index for index, value in enumerate(strings)}
    attributes = [[s["name"], s["frx"]],
                  [s["ident"], s["chase_close"]], [s["type"], s["Mounted"]], [s["hidden"], s["false"]],
                  [s["ident"], s["head-cam"]], [s["type"], s["Head"]], [s["hidden"], s["false"]],
                  [s["name"], s["position"]], [s["type"], s["vector3"]], [s["x"], s["0.3460"]],
                  [s["y"], s["1.0680"]], [s["z"], s["-0.3170"]]]
    elements = [[s["VehicleViewDefinition"], 0, 0, 0, 1, 1],
                [s["ViewManager"], 0, 1, 0, 2, 2],
                [s["View"], 0, 3, 1, 0, 0],
                [s["View"], 0, 3, 4, 1, 4],
                [s["Parameter"], 0, 5, 7, 0, 0]]
    return seat_cameras.encode(seat_cameras.BinXml(strings, elements, attributes))


class SeatCameraTests(unittest.TestCase):
    def test_roundtrip_and_independent_decode(self):
        data = sample()
        self.assertEqual(seat_cameras.encode(seat_cameras.decode(data)), data)
        self.assertEqual(int.from_bytes(data[20:24], "little") % 16, 0)  # string payload padded to 16 bytes
        root = decode_xml(data)
        self.assertEqual(root.find("ViewManager/View[@ident='head-cam']/Parameter").get("y"), "1.0680")

    def test_offset_changes_only_the_position_values(self):
        data = sample()
        moved = seat_cameras.offset_seat(data, 0.0, 0.3, -0.05)
        document = seat_cameras.decode(moved)
        self.assertEqual(seat_cameras.head_position(document)[1], (0.346, 1.368, -0.367))
        original = seat_cameras.decode(data)
        self.assertEqual(document.strings[:len(original.strings)], original.strings)  # no existing string edited
        self.assertEqual(document.elements, original.elements)
        changed = [i for i, (a, b) in enumerate(zip(original.attributes, document.attributes)) if a != b]
        self.assertEqual(changed, [10, 11])  # y and z of the position; x unchanged
        self.assertEqual(seat_cameras.offset_seat(data, 0, 0, 0), data)

    def test_start_in_cockpit_hides_only_views_before_head_cam(self):
        document = seat_cameras.decode(seat_cameras.start_in_cockpit(sample()))
        hidden = {document.attributes_of(v)["ident"]: document.attributes_of(v)["hidden"] for v in seat_cameras.views(document)}
        self.assertEqual(hidden, {"chase_close": "true", "head-cam": "false"})

    def test_refusals(self):
        data = sample()
        for bad in (0.51, -0.6, float("nan"), float("inf"), True):
            with self.assertRaises(ValueError):
                seat_cameras.offset_seat(data, bad, 0, 0)
        for broken in (data[:-1], b"<xml/>", data[:4] + b"\0\0\0\0" + data[8:], data + b"\0"):
            with self.assertRaises(ValueError):
                seat_cameras.decode(broken)

    @unittest.skipUnless((GAME / "cars" / "models").is_dir(), "DiRT 3 is not installed in the default Steam folder")
    def test_every_installed_car(self):
        files = sorted((GAME / "cars" / "models").glob("*/cameras.xml"))
        self.assertGreaterEqual(len(files), 63)
        for path in files:
            data = path.read_bytes()
            with self.subTest(car=path.parent.name):
                self.assertEqual(seat_cameras.encode(seat_cameras.decode(data)), data)
                before = seat_cameras.head_position(seat_cameras.decode(data))[1]
                after = seat_cameras.head_position(seat_cameras.decode(seat_cameras.offset_seat(data, 0.1, -0.2, 0.3)))[1]
                self.assertEqual([round(b - a, 4) for a, b in zip(before, after)], [0.1, -0.2, 0.3])
                self.assertIsNotNone(decode_xml(seat_cameras.offset_seat(data, 0.1, -0.2, 0.3)))


if __name__ == "__main__":
    unittest.main()
