"""Builds the synthetic camera file the launcher's seat tests use (no game data) and the expected
results of tools/seat_cameras.py for a few offsets, so the launcher's port can be checked byte for
byte. Run from the repository root: python tests/launcher/fixtures/seat/make_seat_fixture.py"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "tools"))
import seat_cameras as sc  # noqa: E402

HERE = Path(__file__).resolve().parent


def build() -> bytes:
    strings: list[str] = []

    def s(value: str) -> int:
        if value not in strings:
            strings.append(value)
        return strings.index(value)

    elements: list[list[int]] = []
    attributes: list[list[int]] = []

    def element(name: str, attrs: dict[str, str]) -> int:
        first = len(attributes)
        for key, value in attrs.items():
            attributes.append([s(key), s(value)])
        elements.append([s(name), s(""), len(attrs), first if attrs else 0, 0, 0])
        return len(elements) - 1

    # Elements are stored breadth first, children contiguous, as in the game's files.
    root = element("VehicleViewDefinition", {})
    manager = element("ViewManager", {"version": "1.0000"})
    bonnet = element("View", {"ident": "bonnet-cam", "type": "Bonnet", "hidden": "false"})
    head = element("View", {"ident": "head-cam", "type": "Head", "hidden": "false"})
    reverse = element("View", {"ident": "head-cam_reverse", "type": "Head", "hidden": "true"})
    bonnet_position = element("Parameter", {"name": "position", "type": "vector3", "x": "0.0000", "y": "1.0680", "z": "0.3000"})
    head_position = element("Parameter", {"name": "position", "type": "vector3", "x": "0.3460", "y": "1.0680", "z": "-0.3170"})
    head_fov = element("Parameter", {"name": "fov", "type": "float", "value": "0.3460"})
    reverse_position = element("Parameter", {"name": "position", "type": "vector3", "x": "0.3460", "y": "1.0680", "z": "-0.3170"})

    def children(parent: int, first: int, count: int) -> None:
        elements[parent][4] = count
        elements[parent][5] = first

    children(root, manager, 1)
    children(manager, bonnet, 3)
    children(bonnet, bonnet_position, 1)
    children(head, head_position, 2)
    children(reverse, reverse_position, 1)
    return sc.encode(sc.BinXml(strings, elements, attributes))


def main() -> None:
    original = build()
    assert sc.encode(sc.decode(original)) == original
    (HERE / "cameras.bin").write_bytes(original)
    for name, offsets in {"left-up-forward": (0.1, 0.05, 0.3), "limits": (-0.5, 0.5, -0.5), "centimetre": (0.01, -0.02, 0.07)}.items():
        (HERE / f"cameras-{name}.bin").write_bytes(sc.offset_seat(original, *offsets))
    print("wrote", sorted(p.name for p in HERE.glob("*.bin")))


if __name__ == "__main__":
    main()
