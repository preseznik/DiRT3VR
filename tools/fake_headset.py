"""Lab only: one VR research run on SteamVR's built-in virtual headset (#0ea87e54, DIRT2VR #a3887708).

SteamVR's null driver is a headset without hardware. For one run this switches SteamVR to it, runs
run_benchmark.py with SteamVR's OpenXR runtime given to the game alone (XR_RUNTIME_JSON, so the
system's OpenXR runtime stays as it is), then closes SteamVR and puts steamvr.vrsettings back byte for
byte. The renderer must accept a runtime that names no graphics adapter (src/xr_adapter.h).

    python tools/fake_headset.py --run [run_benchmark.py options, e.g. --msaa 2xmsaa]

Ask in #dirt3vr before a run: SteamVR is switched for its whole length.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STEAM = Path(r"C:\Program Files (x86)\Steam")
VRSETTINGS = STEAM / "config/steamvr.vrsettings"
RUNTIME = STEAM / "steamapps/common/SteamVR/steamxr_win32.json"
STEAMVR_PROCESSES = ("vrserver", "vrmonitor", "vrcompositor", "vrdashboard", "vrwebhelper", "vrstartup")
GAME_PROCESSES = ("dirt3_game", "DiRT3VR")


def null_headset(data: bytes, width: int = 2016, height: int = 2240) -> bytes:
    """steamvr.vrsettings switched to the null driver at width x height per eye, 90 Hz."""
    if not (640 <= width <= 4096 and 640 <= height <= 4096):
        raise ValueError("Eye size must be 640-4096 per side")
    settings = json.loads(data.decode("utf-8-sig"))
    if not isinstance(settings, dict):
        raise ValueError("steamvr.vrsettings is not a JSON object")
    for section in ("steamvr", "power"):
        if not isinstance(settings.setdefault(section, {}), dict):
            raise ValueError(f"steamvr.vrsettings has an unexpected {section} section")
    settings["steamvr"]["forcedDriver"] = "null"
    settings["steamvr"]["activateMultipleDrivers"] = True
    # The virtual headset never moves: without these SteamVR enters standby after 5 s and pauses the
    # compositor, and the session never becomes visible.
    settings["power"]["turnOffScreensTimeout"] = 86400.0
    settings["power"]["pauseCompositorOnStandby"] = False
    # A small mirror window in the corner instead of the driver's 2160x1200 default.
    settings["driver_null"] = {"enable": True, "serialNumber": "DiRT3VR lab", "modelNumber": "Null",
                               "windowX": 0, "windowY": 0, "windowWidth": 640, "windowHeight": 360,
                               "renderWidth": width, "renderHeight": height,
                               "secondsFromVsyncToPhotons": 0.01111111, "displayFrequency": 90.0}
    return json.dumps(settings, indent=3).encode("utf-8")


def restore(target: Path, original: bytes) -> bool:
    """Put the original bytes back; True when the file matches them exactly."""
    target.write_bytes(original)
    return hashlib.sha256(target.read_bytes()).hexdigest() == hashlib.sha256(original).hexdigest()


def running(names: tuple[str, ...]) -> list[str]:
    output = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, check=True).stdout
    found = {line.split(",")[0].strip('"').lower() for line in output.splitlines() if line}
    return [name for name in names if name.lower() + ".exe" in found]


def stop_steamvr() -> bool:
    for name in STEAMVR_PROCESSES:
        subprocess.run(["taskkill", "/f", "/im", name + ".exe"], capture_output=True)
    deadline = time.monotonic() + 30
    while running(STEAMVR_PROCESSES) and time.monotonic() < deadline:
        time.sleep(0.5)
    return not running(STEAMVR_PROCESSES)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--run", action="store_true", help="Switch SteamVR and run; without it, only the checks")
    parser.add_argument("--eye", default="2016x2240", help="The virtual headset's eye size, WIDTHxHEIGHT")
    args, benchmark = parser.parse_known_args()
    width, height = (int(side) for side in args.eye.lower().split("x"))
    if ctypes.windll.user32.GetSystemMetrics(0x1000):  # SM_REMOTESESSION
        raise SystemExit("Remote Desktop session: no game runs over RDP")
    if busy := running(STEAMVR_PROCESSES):
        raise SystemExit(f"SteamVR is running ({', '.join(busy)}); a real headset may be in use. Close it first.")
    if busy := running(GAME_PROCESSES):
        raise SystemExit(f"{', '.join(busy)} is running")
    if not RUNTIME.is_file():
        raise SystemExit(f"SteamVR's OpenXR runtime is missing: {RUNTIME}")
    original = VRSETTINGS.read_bytes()
    switched = null_headset(original, width, height)
    if not args.run:
        print(json.dumps({"ready": True, "eye": f"{width}x{height}", "runtime": str(RUNTIME)}))
        return 0
    evidence = ROOT / "artifacts/fake-vr" / time.strftime("%Y%m%d-%H%M%S")
    evidence.mkdir(parents=True)
    (evidence / "steamvr.vrsettings.original").write_bytes(original)
    result = {"eye": f"{width}x{height}", "steamVrSettingsSha256": hashlib.sha256(original).hexdigest()}
    try:
        VRSETTINGS.write_bytes(switched)
        command = [sys.executable, str(ROOT / "tools/run_benchmark.py"), "--run", "--scene-observer", "--serial-render",
                   "--xr-runtime", str(RUNTIME), *benchmark]
        result["command"] = command[1:]
        completed = subprocess.run(command, capture_output=True, text=True)
        (evidence / "run_benchmark.out").write_text(completed.stdout + completed.stderr, encoding="utf-8")
        result["benchmarkExit"] = completed.returncode
    finally:
        # SteamVR rewrites its settings when it exits, so restore only after all of it has gone.
        result["steamVrStopped"] = stop_steamvr()
        result["steamVrRestored"] = restore(VRSETTINGS, original)
        (evidence / "fake-vr.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps({**result, "evidence": str(evidence)}, indent=2))
    return 0 if result["steamVrRestored"] and result.get("benchmarkExit") == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
