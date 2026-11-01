import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from run_benchmark import bmp_to_png  # noqa: E402


def bmp(width, height, bgra):
    pixels = bytes(bgra)
    header = (b"BM" + struct.pack("<IHHI", 54 + len(pixels), 0, 0, 54)
              + struct.pack("<IiiHHIIiiII", 40, width, -height, 1, 32, 0, len(pixels), 0, 0, 0, 0))
    return header + pixels


class ScreenshotPngTests(unittest.TestCase):
    def test_top_down_bgra_becomes_rgb_png(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "screenshot-1-40s.bmp"
            # red, green / blue, white in BGRA
            source.write_bytes(bmp(2, 2, [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255]))
            png = bmp_to_png(source).read_bytes()
            self.assertEqual(png[:8], b"\x89PNG\r\n\x1a\n")
            self.assertEqual(struct.unpack(">II", png[16:24]), (2, 2))
            idat = png.index(b"IDAT")
            length = struct.unpack(">I", png[idat - 4:idat])[0]
            self.assertEqual(zlib.crc32(png[idat:idat + 4 + length]) & 0xFFFFFFFF,
                             struct.unpack(">I", png[idat + 4 + length:idat + 8 + length])[0])
            raw = zlib.decompress(png[idat + 4:idat + 4 + length])
            self.assertEqual(raw, b"\x00\xff\x00\x00\x00\xff\x00" + b"\x00\x00\x00\xff\xff\xff\xff")

    def test_unexpected_layout_is_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "bad.bmp"
            source.write_bytes(bmp(2, 2, [0] * 16)[:-1])
            with self.assertRaises(ValueError):
                bmp_to_png(source)
            source.write_bytes(b"PNG not a bmp" * 10)
            with self.assertRaises(ValueError):
                bmp_to_png(source)


if __name__ == "__main__":
    unittest.main()
