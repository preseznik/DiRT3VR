"""Check classifier layout guards against the supported PE without executing it."""
import argparse
from pathlib import Path
import re
import struct
from inspect_render_boundary import Pe32


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    args = parser.parse_args()
    pe = Pe32(args.executable)
    source = (Path(__file__).resolve().parents[2] / "src/camera_classifier.cpp").read_text()

    def rva(name):
        return int(re.search(r"\b" + name + r" = (0x[0-9a-f]+)", source).group(1), 16)

    def data(at, count):
        return pe.read_va(pe.base + at, count)[1]

    for name in ("Getter", "Entry", "Selector"):
        text = re.search(name + r"Bytes\[\] = \{([^}]+)\}", source).group(1)
        expected = bytes(int(value, 16) for value in text.split(","))
        assert data(rva(name + "Rva"), len(expected)) == expected, name + " entry guard differs"
    for at, target in ((0x5806d7, rva("EntryRva")), (0x176f4e, 0x7c260), (0x176f5e, 0x7c260),
                       (0x1aecff, rva("SelectorRva")), (0x1aed11, 0x7c260)):
        call = data(at, 5)
        assert call[0] == 0xe8 and at + 5 + struct.unpack_from("<i", call, 1)[0] == target
    for at, target in ((0x176f44, rva("GlobalCameraRva")), (0x176f54, rva("GlobalCameraRva")),
                       (0x1aecee, rva("RootRva")), (rva("HeadTableRva") + 0x30, rva("HeadTypeRva")),
                       (rva("HeadTypeRva") + 1, 0xbd8ad8), (0xbd8ad8, 0xb285cc)):
        assert struct.unpack("<I", data(at, 4))[0] == pe.base + target
    assert data(0x1aecec, 2) == bytes.fromhex("8b 0d")
    assert data(rva("HeadTypeRva"), 1) == b"\xa1" and data(rva("HeadTypeRva") + 5, 1) == b"\xc3"
    assert data(0xb285cc, 5) == b"Head\x00"
    print("Camera classifier: supported SHA-256/x86 PE, entries, copy/publisher calls and Head type guards verified")


if __name__ == "__main__":
    main()
