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


def build_comfort() -> bytes:
    """A cockpit view with the head-cam parameters of Wrench's stock-car inventory (mex, frx; #cd56379b):
    shake and distortion groups, the G-force head and body movement, and the driver switches; the
    look-back view has a driver switch of its own that the comfort edit leaves alone."""
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

    def scalar(name: str, value: str) -> int:
        return element("Parameter", {"name": name, "type": "scalar", "value": value})

    def flag(name: str, value: str) -> int:
        return element("Parameter", {"name": name, "type": "bool", "value": value})

    def children(parent: int, first: int, count: int) -> None:
        elements[parent][4] = count
        elements[parent][5] = first

    root = element("VehicleViewDefinition", {})
    manager = element("ViewManager", {"version": "1.0000"})
    head = element("View", {"ident": "head-cam", "type": "Head", "reverseCamera": "head-cam_reverse", "hidden": "false"})
    reverse = element("View", {"ident": "head-cam_reverse", "type": "Mounted", "hidden": "true"})
    shake = element("AccelerationBasedShake", {})
    distortion = element("Distortion", {})
    head_params = [scalar("fov", "50.0000"), scalar("nearPlane", "0.0750"), flag("DisableDriverHead", "true"), flag("DisableDriver", "false"),
                   flag("DisableCoDriver", "false"), scalar("maxBodyOffset", "0.0060"), scalar("maxHeadOffset", "0.1350"), scalar("headDamping", "1.7000"),
                   flag("headBuffeting", "true"), element("Parameter", {"name": "position", "type": "vector3", "x": "0.3460", "y": "1.0680", "z": "-0.3170"})]
    reverse_params = [flag("DisableDriver", "false"), element("Parameter", {"name": "position", "type": "vector3", "x": "0.3460", "y": "1.0680", "z": "-0.3170"})]
    shake_params = [scalar("shakeAngleScalingX", "0.0002"), scalar("minShakeAngleX", "0.1422"), scalar("maxShakeAngleX", "0.2000"),
                    scalar("shakeAngleScalingY", "0.0004"), scalar("minShakeAngleY", "0.1422"), scalar("maxShakeAngleY", "0.2000"),
                    scalar("shakeMovementScalingX", "0.0000"), flag("delayEnabled", "false")]
    distortion_params = [flag("cdEnabled", "true"), scalar("cdLowAcceleration", "5.0000")]
    children(root, manager, 1)
    children(manager, head, 2)
    children(head, shake, 2 + len(head_params))
    children(reverse, reverse_params[0], len(reverse_params))
    children(shake, shake_params[0], len(shake_params))
    children(distortion, distortion_params[0], len(distortion_params))
    return sc.encode(sc.BinXml(strings, elements, attributes))


def main() -> None:
    original = build()
    assert sc.encode(sc.decode(original)) == original
    (HERE / "cameras.bin").write_bytes(original)
    for name, offsets in {"left-up-forward": (0.1, 0.05, 0.3), "limits": (-0.5, 0.5, -0.5), "centimetre": (0.01, -0.02, 0.07)}.items():
        (HERE / f"cameras-{name}.bin").write_bytes(sc.offset_seat(original, *offsets))
    # Start in the cockpit view: after the seat offset (zero offset re-encodes the original unchanged).
    (HERE / "cameras-cockpit.bin").write_bytes(sc.start_in_cockpit(sc.offset_seat(original, 0, 0, 0)))
    (HERE / "cameras-left-up-forward-cockpit.bin").write_bytes(sc.start_in_cockpit(sc.offset_seat(original, 0.1, 0.05, 0.3)))
    # Start in the cockpit view without hiding anything (cockpit_first, iztok 2026-11-03): head-cam moved first.
    (HERE / "cameras-first.bin").write_bytes(sc.cockpit_first(sc.offset_seat(original, 0, 0, 0)))
    (HERE / "cameras-left-up-forward-first.bin").write_bytes(sc.cockpit_first(sc.offset_seat(original, 0.1, 0.05, 0.3)))
    comfort = build_comfort()
    assert sc.encode(sc.decode(comfort)) == comfort
    (HERE / "cameras-comfort.bin").write_bytes(comfort)
    print("wrote", sorted(p.name for p in HERE.glob("*.bin")))


if __name__ == "__main__":
    main()
