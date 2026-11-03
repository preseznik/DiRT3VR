"""Lab only: one VR run on SteamVR's built-in virtual headset (#0ea87e54, DIRT2VR #a3887708).

SteamVR's null driver is a headset without hardware. For one run this switches SteamVR to it, runs the
game with SteamVR's OpenXR runtime given to that run alone (the system's OpenXR runtime stays as it is),
then closes SteamVR and puts steamvr.vrsettings back byte for byte. The renderer must accept a runtime
that names no graphics adapter (src/xr_adapter.h).

    python tools/fake_headset.py --run [run_benchmark.py options, e.g. --msaa 2xmsaa]

With --launcher it runs a real Launch VR session instead (DiRT3VR.exe --launch --vr with this PC's saved
settings) and stops DiRT 3 after --play seconds. --set Name=JSON changes a Vr*, Hud* or Perf* setting for
that run only, and settings.json is put back byte for byte too:

    python tools/fake_headset.py --run --launcher <package>/DiRT3VR/DiRT3VR.exe --set VrFieldOfView=85

Ask in #dirt3vr before a run: SteamVR is switched for its whole length.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STEAM = Path(r"C:\Program Files (x86)\Steam")
VRSETTINGS = STEAM / "config/steamvr.vrsettings"
RUNTIME = STEAM / "steamapps/common/SteamVR/steamxr_win32.json"
GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition")
STEAMVR_PROCESSES = ("vrserver", "vrmonitor", "vrcompositor", "vrdashboard", "vrwebhelper", "vrstartup")
GAME_PROCESSES = ("dirt3_game", "DiRT3VR")
SETTABLE = re.compile(r"(Vr|Hud|Perf)[A-Za-z]+")


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


def user_root(game: Path, local: Path) -> Path:
    """The launcher's settings folder for a game folder (launcher/Core.vb InstallContext)."""
    key = str(game).rstrip("\\/").upper()
    return local / "DiRT3VR" / hashlib.sha256(key.encode("utf-8")).hexdigest().upper()[:24]


def with_settings(data: bytes, changes: dict) -> bytes:
    """settings.json with only Vr*, Hud* and Perf* settings changed, each keeping its saved type."""
    settings = json.loads(data.decode("utf-8-sig"))
    if not isinstance(settings, dict):
        raise ValueError("settings.json is not a JSON object")
    for name, value in changes.items():
        if not SETTABLE.fullmatch(name):
            raise ValueError(f"{name} is not a VR, HUD or performance setting")
        if name in settings and type(settings[name]) is not type(value):
            raise ValueError(f"{name} must stay a {type(settings[name]).__name__}")
        settings[name] = value
    return json.dumps(settings, indent=2).encode("utf-8")


def parse_setting(text: str) -> tuple[str, object]:
    name, separator, value = text.partition("=")
    if not separator:
        raise argparse.ArgumentTypeError("use Name=JSON, e.g. VrFieldOfView=85 or VrHelmetView=true")
    try:
        return name, json.loads(value)
    except json.JSONDecodeError:
        return name, value


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


def launch_session(launcher: Path, play: int, evidence: Path, logs: Path, result: dict) -> None:
    """One Launch VR session through the launcher's command line, stopped after play seconds of game."""
    before = {path.name for path in logs.iterdir()} if logs.is_dir() else set()
    command = [str(launcher), "--launch", "--vr", "--runtime", "other", "--manifest", str(RUNTIME)]
    result["command"] = command
    with (evidence / "launcher.out").open("wb") as output:
        process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, cwd=launcher.parent)
        deadline = time.monotonic() + 180
        while not running(("dirt3_game",)) and process.poll() is None and time.monotonic() < deadline:
            time.sleep(1)
        result["gameStarted"] = bool(running(("dirt3_game",)))
        if result["gameStarted"]:
            deadline = time.monotonic() + play
            while running(("dirt3_game",)) and time.monotonic() < deadline:
                time.sleep(1)
            result["gameAliveAtEnd"] = bool(running(("dirt3_game",)))
            subprocess.run(["taskkill", "/f", "/im", "dirt3_game.exe"], capture_output=True)
        try:
            result["launcherExit"] = process.wait(timeout=120)
        except subprocess.TimeoutExpired:
            process.kill()
            result["launcherExit"] = None
    if logs.is_dir():
        for session in sorted(path for path in logs.iterdir() if path.name not in before and path.is_dir()):
            (evidence / session.name).mkdir()
            for file in session.iterdir():
                if file.is_file():
                    (evidence / session.name / file.name).write_bytes(file.read_bytes())
            result.setdefault("sessions", []).append(session.name)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--run", action="store_true", help="Switch SteamVR and run; without it, only the checks")
    parser.add_argument("--eye", default="2016x2240", help="The virtual headset's eye size, WIDTHxHEIGHT")
    parser.add_argument("--launcher", type=Path, help="A packaged DiRT3VR.exe: a real Launch VR session instead of the research runner")
    parser.add_argument("--play", type=int, default=120, help="With --launcher: seconds of game before DiRT 3 is stopped (30-300)")
    parser.add_argument("--set", type=parse_setting, action="append", default=[], metavar="NAME=JSON",
                        help="With --launcher: a Vr*, Hud* or Perf* setting for this run only")
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
    launcher_settings = user_root(GAME, Path(os.environ["LOCALAPPDATA"])) / "settings.json"
    if args.launcher:
        if not args.launcher.is_file() or not (args.launcher.parent / "payload/d3d11.dll").is_file():
            raise SystemExit(f"Not a packaged launcher with its payload: {args.launcher}")
        if not 30 <= args.play <= 300:
            raise SystemExit("--play must be 30-300 seconds")
        if benchmark:
            raise SystemExit(f"Not a launcher option: {' '.join(benchmark)}")
        saved = launcher_settings.read_bytes()
        changed = with_settings(saved, dict(args.set))
    elif args.set:
        raise SystemExit("--set needs --launcher")
    if not args.run:
        print(json.dumps({"ready": True, "eye": f"{width}x{height}", "runtime": str(RUNTIME),
                          "launcher": str(args.launcher) if args.launcher else None, "settings": dict(args.set)}))
        return 0
    evidence = ROOT / "artifacts/fake-vr" / time.strftime("%Y%m%d-%H%M%S")
    evidence.mkdir(parents=True)
    (evidence / "steamvr.vrsettings.original").write_bytes(original)
    result = {"eye": f"{width}x{height}", "steamVrSettingsSha256": hashlib.sha256(original).hexdigest()}
    if args.launcher:
        (evidence / "launcher-settings.original.json").write_bytes(saved)
        result.update(settings=dict(args.set), launcherSettingsSha256=hashlib.sha256(saved).hexdigest())
    try:
        VRSETTINGS.write_bytes(switched)
        if args.launcher:
            launcher_settings.write_bytes(changed)
            launch_session(args.launcher, args.play, evidence, launcher_settings.parent / "logs", result)
        else:
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
        if args.launcher:
            result["launcherSettingsRestored"] = restore(launcher_settings, saved)
        (evidence / "fake-vr.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps({**result, "evidence": str(evidence)}, indent=2))
    finished = result.get("launcherExit") == 0 if args.launcher else result.get("benchmarkExit") == 0
    return 0 if result["steamVrRestored"] and result.get("launcherSettingsRestored", True) and finished else 1


if __name__ == "__main__":
    raise SystemExit(main())
