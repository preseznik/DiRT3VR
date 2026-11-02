"""Builds the synthetic effects file the launcher's motion blur tests use (Lens's sample from
tests/test_motion_blur.py, no game data) and tools/motion_blur.py's result for it, so the
launcher's port can be checked byte for byte. Run from the repository root:
python tests/launcher/fixtures/effects/make_effects_fixture.py"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "tests"))
import motion_blur  # noqa: E402
import seat_cameras  # noqa: E402
import test_motion_blur  # noqa: E402
from seat_cameras import Node  # noqa: E402

HERE = Path(__file__).resolve().parent


def main() -> None:
    original = test_motion_blur.sample()
    (HERE / "effects.bin").write_bytes(original)
    (HERE / "effects-no-blur.bin").write_bytes(motion_blur.disable_motion_blur(original))
    no_lengths = seat_cameras.encode(seat_cameras.from_tree(Node("PostProcessEffects", None, [], [
        Node("Effect", None, [("name", "plain")], [])]), [""]))
    (HERE / "effects-without-motion-blur.bin").write_bytes(no_lengths)
    print("wrote", sorted(p.name for p in HERE.glob("*.bin")))


if __name__ == "__main__":
    main()
