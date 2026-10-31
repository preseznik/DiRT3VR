"""Bounded, reversible DiRT 3 proxy diagnostics.

Default is read-only preflight. --run starts only the selected game executable;
there is no input automation. An OpenXR session requires explicit --xr-runtime
opt-in. --recover repairs an interrupted deployment after the game closes.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET

from diagnostic_files import (DiagnosticFiles, RESTRICTIONS_NAME, no_links,
                              preflight_serial_render, restore_xml_formatting, save_json, sha256)

EXPECTED_EXE = "c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98"
ROOT = Path(__file__).resolve().parents[1]
REPLAY_PAIR_COUNT = 120
REPLAY_SAMPLE_OFFSETS = (0, 60, 119)
REPLAY_LINE = re.compile(
    r"Replay pair main=(\d+) ready=(\d+) restored=(\d+) calls=(\d+) sampled=(\d+) "
    r"history_before=(\d+) history_first=(\d+) history_second=(\d+) same_frame=(\d+) "
    r"vertices=(\d+)/(\d+) primitives=(\d+)/(\d+) pixels=(\d+)/(\d+) "
    r"present_enter=(\d+) present_exit=(\d+) stereo_verified=false")


def summarize_replay(logs: str, directory: Path) -> dict:
    """Validate the bounded native experiment; this never establishes VR support."""
    errors = []
    pairs = []
    for line in logs.splitlines():
        if "Replay refused" in line or "Replay failed" in line:
            errors.append(line.strip())
        if "Replay pair " not in line:
            continue
        match = REPLAY_LINE.fullmatch(line[line.index("Replay pair "):].strip())
        if match is None:
            errors.append("Malformed replay-pair evidence")
            continue
        (scene, ready, restored, calls, sampled, before, first, second, same_frame,
         vertices_l, vertices_r, primitives_l, primitives_r, pixels_l, pixels_r,
         present_enter, present_exit) = map(int, match.groups())
        valid = (scene >= 1 and ready == restored == same_frame == 1 and calls == 2
                 and sampled == int(len(pairs) in REPLAY_SAMPLE_OFFSETS)
                 and first == second == ((before + 1) & 0xffffffff) and present_enter == present_exit
                 and (not sampled or min(vertices_l, vertices_r, primitives_l, primitives_r, pixels_l, pixels_r) > 0))
        pairs.append({"scene": scene, "valid": valid, "sampled": bool(sampled),
                      "historyBefore": before, "historyFirst": first, "historySecond": second,
                      "vertices": [vertices_l, vertices_r], "primitives": [primitives_l, primitives_r],
                      "pixels": [pixels_l, pixels_r], "presentEnter": present_enter, "presentExit": present_exit})
    first_scene = pairs[0]["scene"] if pairs else 0
    if (first_scene < 1 or [pair["scene"] for pair in pairs] != list(range(first_scene, first_scene + REPLAY_PAIR_COUNT))
            or not all(pair["valid"] for pair in pairs)):
        errors.append("Replay requires exactly 120 valid, consecutive scene pairs with a positive starting scene")
    captures = []
    sample_scenes = [first_scene + offset for offset in REPLAY_SAMPLE_OFFSETS] if first_scene >= 1 else []
    expected = {f"replay-{scene}-eye{eye}.bmp" for scene in sample_scenes for eye in (0, 1)}
    if {path.name for path in directory.glob("replay-*.bmp")} != expected:
        errors.append("Replay requires exactly six captures at pair offsets 0, 60 and 119")
    for name in sorted(expected):
        path = directory / name
        try:
            no_links(path)
            with path.open("rb") as stream:
                header = stream.read(54)
            if len(header) != 54:
                raise ValueError("truncated BMP header")
            signature, size, _, _, offset = struct.unpack_from("<2sIHHI", header)
            dib, width, height, planes, bits, compression, image_size = struct.unpack_from("<IiiHHII", header, 14)
            expected_size = 1280 * 720 * 4
            if (signature != b"BM" or offset != 54 or dib != 40 or width != 1280 or height != -720
                    or planes != 1 or bits != 32 or compression != 0 or image_size != expected_size
                    or size != offset + expected_size or path.stat().st_size != size):
                raise ValueError("unexpected or incomplete native BMP")
            captures.append({"name": name, "sha256": sha256(path), "bytes": size})
        except (OSError, ValueError) as error:
            errors.append(f"Invalid replay capture {name}: {error}")
    return {"evidenceVerified": not errors, "pairCount": len(pairs),
            "validPairCount": sum(pair["valid"] for pair in pairs),
            "sampledScenes": [pair["scene"] for pair in pairs if pair["sampled"]],
            "pairs": pairs, "captures": captures, "errors": errors, "stereoVerified": False}


def preflight_xr_manifest(path: Path) -> dict:
    """Read the explicit manifest and prove its library is an x86 Windows DLL."""
    if not path.is_absolute() or path.suffix.lower() != ".json":
        raise ValueError("--xr-runtime requires an absolute runtime JSON manifest path")
    no_links(path)
    if not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ValueError("Runtime manifest is missing or larger than 1 MiB")
    data = path.read_bytes()
    document = json.loads(data.decode("utf-8-sig"))
    runtime = document.get("runtime") if isinstance(document, dict) else None
    library_name = runtime.get("library_path") if isinstance(runtime, dict) else None
    if not isinstance(library_name, str) or not library_name.strip() or "\0" in library_name:
        raise ValueError("Runtime manifest has no valid library_path")
    library = (path.parent / library_name).absolute()
    no_links(library)
    with library.open("rb") as stream:
        size = library.stat().st_size
        dos = stream.read(64)
        if len(dos) != 64 or dos[:2] != b"MZ":
            raise ValueError("Runtime library is not a Windows DLL")
        pe = struct.unpack_from("<I", dos, 0x3c)[0]
        if not 64 <= pe <= size - 26:
            raise ValueError("Runtime library has an invalid PE header")
        stream.seek(pe)
        header = stream.read(26)
        if (header[:4] != b"PE\0\0" or struct.unpack_from("<H", header, 4)[0] != 0x14c
                or struct.unpack_from("<H", header, 20)[0] < 2
                or not struct.unpack_from("<H", header, 22)[0] & 0x2000
                or struct.unpack_from("<H", header, 24)[0] != 0x10b):
            raise ValueError("DiRT 3 requires a 32-bit (x86 PE32) OpenXR runtime DLL")
    return {"manifest": str(path), "manifestSha256": hashlib.sha256(data).hexdigest(),
            "library": str(library), "librarySha256": sha256(library), "architecture": "x86"}


def summarize_xr(logs: str) -> dict:
    """Check prototype submission/restore evidence, never headset acceptance."""
    errors, frames = [], []
    required = {"main", "submitted", "total", "cameras_restored", "history_before", "history_first",
                "history_second", "history_expected_advance", "projections"}
    for line in logs.splitlines():
        if "XR prototype failed" in line or "XR prototype refused" in line:
            errors.append(line.strip())
        if "XR prototype stopped reason=" in line and not line.strip().endswith("reason=runner-stop-file"):
            errors.append(line.strip())
        if "XR prototype main=" not in line:
            continue
        text = line[line.index("XR prototype main=") + len("XR prototype "):]
        tokens = text.strip().split()
        fields = dict(token.split("=", 1) for token in tokens if "=" in token)
        try:
            if len(fields) != len(tokens) or not required <= fields.keys():
                raise ValueError("missing or duplicate fields")
            values = {name: int(fields[name]) for name in required - {"projections"}}
            projections = [int(value) for value in fields["projections"].split("/")]
            before, first, second = (values[name] for name in ("history_before", "history_first", "history_second"))
            advance = values["history_expected_advance"]
            history_ok = (all(0 <= value <= 0xffffffff for value in (before, first, second)) and first == second
                          and advance in (0, 1) and first == ((before + advance) & 0xffffffff))
            valid = (values["main"] > 0 and values["total"] > 0 and values["submitted"] == 1
                     and values["cameras_restored"] == 1 and len(projections) == 2 and min(projections) > 0
                     and history_ok
                     and (not frames or (values["main"] > frames[-1]["main"] and values["total"] > frames[-1]["total"])))
            frames.append({**values, "projections": projections, "historyExpectedAdvance": advance, "valid": valid})
        except (ValueError, KeyError):
            errors.append("Malformed XR prototype submission evidence")
    if not frames or not all(frame["valid"] for frame in frames) or frames[-1]["total"] < 120:
        errors.append("XR prototype requires valid restored submissions with at least 120 total submitted frames")
    shutdown = "XR prototype shutdown complete" in logs
    if not shutdown or "XR prototype stopped reason=runner-stop-file" not in logs:
        errors.append("XR prototype did not confirm runner-requested shutdown on the render thread")
    return {"evidenceVerified": not errors, "loggedFrames": frames,
            "submittedFrames": frames[-1]["total"] if frames else 0, "shutdownVerified": shutdown,
            "errors": errors, "stereoVerified": False}


def native_logs(directory: Path) -> str:
    return "\n".join(path.read_text(errors="replace") for path in sorted(directory.glob("native-*.log")))


def request_xr_shutdown(process, directory: Path, timeout: float = 3.0, *, already_requested: bool = False) -> bool:
    """Request cleanup while the owned child's render thread can still run."""
    if process.poll() is not None:
        return False
    stop = directory / "xr-stop.request"
    if not already_requested:
        with stop.open("xb") as stream:
            stream.write(b"runner-requested shutdown\n")
    deadline = time.monotonic() + timeout
    while True:
        if "XR prototype shutdown complete" in native_logs(directory):
            return True
        if process.poll() is not None or time.monotonic() >= deadline:
            return False
        time.sleep(0.1)


def diagnostic_environment(directory: Path, *, scene_observer: bool = False, replay_pair: bool = False,
                           xr_runtime: Path | None = None) -> dict:
    env = dict(os.environ, DIRT3VR_DIAGNOSTICS="1", DIRT3VR_LOG_DIR=str(directory), SteamAppId="321040")
    for flag in ("DIRT3VR_ENABLE_VR", "DIRT3VR_SCENE_DIAGNOSTICS", "DIRT3VR_REPLAY_DIAGNOSTICS",
                 "DIRT3VR_XR_PROTOTYPE", "DIRT3VR_XR_STOP_FILE", "XR_RUNTIME_JSON"):
        env.pop(flag, None)
    if scene_observer:
        env["DIRT3VR_SCENE_DIAGNOSTICS"] = "1"
    if replay_pair:
        env["DIRT3VR_REPLAY_DIAGNOSTICS"] = "1"
    if xr_runtime is not None:
        env["DIRT3VR_XR_PROTOTYPE"] = "1"
        env["XR_RUNTIME_JSON"] = str(xr_runtime)
        env["DIRT3VR_XR_STOP_FILE"] = str(directory / "xr-stop.request")
    return env


def summarize_benchmark(path: Path, *, serial_render: bool = False) -> dict:
    """Check game-produced results instead of treating a filename as acceptance."""
    root = ET.parse(path).getroot()
    tracks = root.findall("track")
    settings = root.find("track/settings")
    results = root.find("track/results")
    cars = settings.findall("car") if settings is not None else []
    confirmed = bool(root.tag == "benchmark" and len(tracks) == 1
                     and settings is not None and settings.get("name") == "aspen"
                     and settings.get("route") == "route_0" and len(cars) == 1
                     and cars[0].get("name") == "frx" and results is not None
                     and int(results.get("samples", "0")) > 0)
    hardware = root.find("hardware_settings_config")
    expected = {"graphics_card/directx": {"forcedx9": "false"},
                "graphics_card/stereo": {"enabled": "false"},
                "graphics_card/resolution": {"width": "1280", "height": "720", "fullscreen": "false",
                                             "vsync": "0", "multisampling": "off"}}
    if serial_render:
        expected["cpu/threadStrategy"] = {"parallelUpdateRender": "false"}
    scheduling = hardware.find("cpu/threadStrategy") if hardware is not None else None
    observed = {"cpu/threadStrategy/@parallelUpdateRender": scheduling.get("parallelUpdateRender") if scheduling is not None else None}
    mismatches = []
    for location, values in expected.items():
        element = hardware.find(location) if hardware is not None else None
        for name, value in values.items():
            key = location + "/@" + name
            observed[key] = element.get(name) if element is not None else None
            if observed[key] != value:
                mismatches.append(key)
    return {"sceneConfirmed": confirmed, "directxVersion": root.get("directx_version"),
            "samples": int(results.get("samples", "0")) if results is not None else 0,
            "requestedConfigurationVerified": confirmed and root.get("directx_version") == "11" and not mismatches,
            "serialRenderRequested": serial_render,
            "serialRenderVerified": confirmed and observed["cpu/threadStrategy/@parallelUpdateRender"] == "false",
            "observedConfiguration": observed, "configurationMismatches": mismatches}


def diagnostic_hardware(data: bytes, *, serial_render: bool = False) -> bytes:
    """Build the unique diagnostic input without writing the user's settings file."""
    root = ET.fromstring(data)
    required = ["cpu/threadStrategy", "graphics_card/directx", "graphics_card/stereo", "graphics_card/resolution"]
    if root.tag != "hardware_settings_config" or any(len(root.findall(path)) != 1 for path in required):
        raise ValueError("Unexpected game hardware settings structure; no files changed")
    if serial_render:
        root.find("cpu/threadStrategy").set("parallelUpdateRender", "false")
    root.find("graphics_card/directx").set("forcedx9", "false")
    root.find("graphics_card/stereo").set("enabled", "false")
    for key, value in {"width": "1280", "height": "720", "fullscreen": "false", "vsync": "0", "multisampling": "off"}.items():
        root.find("graphics_card/resolution").set(key, value)
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def benchmark_completed(report: dict) -> bool:
    summary = report.get("benchmarkSummary", {})
    return bool(report.get("supportedProxyLoaded") and report.get("lastLoggedPresentCount", 0) > 0
                and report.get("exitCode") == 0 and not report.get("forcedTermination")
                and report.get("benchmarkConfirmed") and summary.get("requestedConfigurationVerified")
                and (not report.get("serialRender") or summary.get("serialRenderVerified"))
                and (not report.get("replayPair") or report.get("replaySummary", {}).get("evidenceVerified"))
                and (not report.get("xrRuntime") or (report.get("xrSummary", {}).get("evidenceVerified")
                                                     and report.get("xrShutdownAcknowledged"))))


def game_running() -> bool:
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
         "@(Get-Process dirt3_game -ErrorAction SilentlyContinue).Count"],
        capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return int(result.stdout.strip()) > 0


def request_close(pid: int) -> None:
    """Close only windows owned by our child; never send keyboard/mouse input."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    @callback_type
    def close(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            user32.PostMessageW(hwnd, 0x10, 0, 0)  # WM_CLOSE
        return True
    user32.EnumWindows(close, 0)


def documents_dir() -> Path:
    # Matches the game's observed SHGetFolderPath(CSIDL_PERSONAL) mapping.
    buffer = ctypes.create_unicode_buffer(32768)
    shell32 = ctypes.WinDLL("shell32")
    shell32.SHGetFolderPathW.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.HANDLE,
                                       wintypes.DWORD, wintypes.LPWSTR]
    result = shell32.SHGetFolderPathW(None, 5, None, 0, buffer)
    if result != 0 or not buffer.value:
        raise OSError("Cannot resolve the game's Documents directory")
    return Path(buffer.value)


def protected_state(game: Path) -> dict[str, str]:
    paths = [game / "dirt3_game.exe", game / "steam_api.dll",
             game / RESTRICTIONS_NAME,
             game / "hardwaresettings/hardware_settings_config.xml",
             game / "hardwaresettings/hardware_settings_info.xml"]
    documents = documents_dir() / "My Games/DiRT3"
    if documents.exists():
        paths += [p for p in documents.rglob("*") if p.is_file() and "benchmarks" not in p.parts]
    # Hash the title's Steam data, but never print account identifiers or modify saves.
    user_data = game.parents[2] / "userdata"
    if user_data.exists():
        for title in user_data.glob("*/321040"):
            paths += [p for p in title.rglob("*") if p.is_file()]
    return {str(p): sha256(p) for p in paths if p.is_file()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, default=Path(r"C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition"))
    parser.add_argument("--proxy", type=Path, default=ROOT / "build/native/bin/d3d11.dll")
    parser.add_argument("--seconds", type=int, default=90)
    parser.add_argument("--run", action="store_true")
    parser.add_argument("--scene-observer", action="store_true", help="Enable guarded, read-only scene-boundary observations")
    parser.add_argument("--serial-render", action="store_true",
                        help="Temporarily disable the fingerprinted CPU parallel-render restrictions; require the completed result to confirm serial scheduling")
    parser.add_argument("--replay-pair", action="store_true",
                        help="Run the bounded desktop eye replay; requires --serial-render and --scene-observer; does not enable VR")
    parser.add_argument("--xr-runtime", type=Path, metavar="ABS_MANIFEST",
                        help="Opt into the headset prototype with an explicit x86 runtime manifest; requires --serial-render and --scene-observer; incompatible with --replay-pair")
    parser.add_argument("--recover", type=Path, metavar="JOURNAL")
    args = parser.parse_args()
    if args.replay_pair and not (args.serial_render and args.scene_observer):
        parser.error("--replay-pair requires both --serial-render and --scene-observer")
    if args.xr_runtime and (not (args.serial_render and args.scene_observer) or args.replay_pair):
        parser.error("--xr-runtime requires --serial-render and --scene-observer and cannot be combined with --replay-pair")
    if args.xr_runtime and not 120 <= args.seconds <= 300:
        parser.error("--xr-runtime requires --seconds between 120 and 300 for a bounded session and shutdown")
    game = args.game.absolute()
    no_links(game)
    if os.name != "nt":
        raise RuntimeError("This runner requires Windows")
    if game_running():
        raise RuntimeError("Close DiRT 3 before preparing or recovering a diagnostic")
    hardware_dir = documents_dir() / "My Games/DiRT3/hardwaresettings"
    no_links(hardware_dir)
    if args.recover:
        conflicts = DiagnosticFiles.recover(args.recover, game, hardware_dir)
        print(json.dumps({"recovered": not conflicts, "conflicts": conflicts}))
        return 1 if conflicts else 0
    if not 15 <= args.seconds <= 300:
        raise ValueError("Duration must be between 15 and 300 seconds")
    exe = game / "dirt3_game.exe"
    if sha256(exe) != EXPECTED_EXE:
        raise ValueError("Unsupported game executable; no files changed")
    no_links(args.proxy)
    if not args.proxy.is_file():
        raise FileNotFoundError("Build the native proxy before preflight")
    if (game / "d3d11.dll").exists():
        raise FileExistsError("Existing graphics proxy preserved; diagnostic deployment refused")
    proxy_bytes = args.proxy.read_bytes()
    hardware_source = hardware_dir / "hardware_settings_config.xml"
    no_links(hardware_source)
    hardware_bytes = diagnostic_hardware(hardware_source.read_bytes(), serial_render=args.serial_render)
    restrictions = preflight_serial_render(game) if args.serial_render else None
    xr_runtime = preflight_xr_manifest(args.xr_runtime) if args.xr_runtime else None
    preflight = {"gameSha256": EXPECTED_EXE, "proxySha256": hashlib.sha256(proxy_bytes).hexdigest(),
                 "mode": "headset-prototype" if xr_runtime else "desktop-eye-replay" if args.replay_pair else "desktop-observation",
                 "stereo": False, "sceneObserver": args.scene_observer,
                 "serialRender": args.serial_render, "replayPair": args.replay_pair,
                 "xrRuntime": xr_runtime,
                 "durationSeconds": args.seconds}
    if restrictions:
        preflight["restrictionsOriginalSha256"] = hashlib.sha256(restrictions[0]).hexdigest()
        preflight["restrictionsAppliedSha256"] = hashlib.sha256(restrictions[1]).hexdigest()
    if not args.run:
        print(json.dumps({"preflight": True, **preflight}, indent=2))
        return 0

    run_id = uuid.uuid4().hex
    run = ROOT / "artifacts/diagnostics" / (time.strftime("%Y%m%d-%H%M%S-") + run_id[:8])
    run.mkdir(parents=True)
    before = protected_state(game)
    # Preserve all protected originals for inspection if the game writes any of them.
    backups = run / "protected-originals"
    backups.mkdir()
    originals = {}
    for index, (path, digest) in enumerate(before.items()):
        name = f"{index:04d}.bin"
        (backups / name).write_bytes(Path(path).read_bytes())
        originals[path] = {"sha256": digest, "backup": name}
    save_json(run / "protected-state.json", originals)
    transaction = DiagnosticFiles(game, run / "deployment.json", run_id, hardware_dir)
    process = None
    hardware_path = None
    hardware_input = run / "hardware-settings-input.xml"
    benchmark_results = documents_dir() / "My Games/DiRT3/benchmarks"
    report = {"schema": 1, **preflight, "runId": run_id, "completed": False,
              "stereoVerified": False, "benchmarkConfirmed": False,
              "forcedTermination": False, "errors": []}
    try:
        if restrictions:
            transaction.apply_serial_render(restrictions[0])
        hardware_name = f"dirt3vr-hardware-{run_id}.xml"
        hardware_input.write_bytes(hardware_bytes)
        hardware_path = transaction.add(hardware_name, hardware_input.read_bytes())
        benchmark = ET.Element("config", {"skipreplays": "true", "outputfilename": f"DiRT3VR_{run_id}",
                                           "hardwaresettings": hardware_name})
        track = ET.SubElement(benchmark, "track", {"country": "usa", "name": "aspen", "route": "route_0"})
        ET.SubElement(track, "car", {"name": "frx", "number": "1"})
        benchmark_path = transaction.add(f"dirt3vr-benchmark-{run_id}.xml", ET.tostring(benchmark, encoding="utf-8", xml_declaration=True))
        transaction.add("d3d11.dll", proxy_bytes)
        env = diagnostic_environment(run, scene_observer=args.scene_observer, replay_pair=args.replay_pair,
                                     xr_runtime=args.xr_runtime)
        started = time.monotonic()
        process = subprocess.Popen([str(exe), "-benchmark", benchmark_path.name], cwd=game, env=env)
        report["pid"] = process.pid
        save_json(run / "run.json", report)
        print(f"Diagnostic running: pid={process.pid}; log directory={run}", flush=True)
        result_seen_at = None
        while process.poll() is None and time.monotonic() - started < args.seconds:
            time.sleep(1)
            if (xr_runtime and not report.get("xrShutdownRequested")
                    and time.monotonic() - started >= min(90, args.seconds - 10)):
                # The benchmark may exit naturally. Stop XR while its render thread
                # is still active, then leave the desktop benchmark to finish.
                report["xrShutdownRequested"] = True
                report["xrShutdownAtSeconds"] = round(time.monotonic() - started, 2)
                report["xrShutdownAcknowledged"] = request_xr_shutdown(process, run)
            if benchmark_results.exists() and any(benchmark_results.glob(f"DiRT3VR_{run_id}*")):
                result_seen_at = result_seen_at or time.monotonic()
                if time.monotonic() - result_seen_at >= 5:
                    break  # Allow the game to finish flushing its completed result.
        if process.poll() is None:
            if xr_runtime and not report.get("xrShutdownAcknowledged"):
                already_requested = bool(report.get("xrShutdownRequested"))
                report["xrShutdownRequested"] = True
                report["xrShutdownAcknowledged"] = request_xr_shutdown(process, run, already_requested=already_requested)
            report["closeRequested"] = True
            request_close(process.pid)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                report["forcedTermination"] = True
                process.kill()  # Only the retained, owned process handle.
                process.wait(timeout=10)
        report["exitCode"] = process.returncode
        report["elapsedSeconds"] = round(time.monotonic() - started, 2)
        logs = native_logs(run)
        report["supportedProxyLoaded"] = "supported_host=1" in logs
        counts = [int(x) for x in re.findall(r"Present count=(\d+)", logs)]
        report["lastLoggedPresentCount"] = max(counts, default=0)
        report["requestedSwapchainObserved"] = bool(re.search(r"Swapchain.*size=1280x720\b", logs))
        if args.replay_pair:
            report["replaySummary"] = summarize_replay(logs, run)
            report["errors"].extend(report["replaySummary"]["errors"])
        if xr_runtime:
            report["xrSummary"] = summarize_xr(logs)
            report["errors"].extend(report["xrSummary"]["errors"])
        results = list(benchmark_results.glob(f"DiRT3VR_{run_id}*")) if benchmark_results.exists() else []
        report["benchmarkResultFiles"] = [p.name for p in results if p.is_file()]
        for result in results:
            if result.is_file():
                (run / result.name).write_bytes(result.read_bytes())
                summary = summarize_benchmark(result, serial_render=args.serial_render)
                report["benchmarkSummary"] = summary
                report["benchmarkConfirmed"] = summary["sceneConfirmed"]
                report["serialRenderVerified"] = summary["serialRenderVerified"]
                if not summary["requestedConfigurationVerified"]:
                    report["errors"].append("The benchmark did not retain the requested scheduling/graphics configuration")
        if not report["benchmarkConfirmed"]:
            report["errors"].append("No completed benchmark result confirmed the requested scene")
        report["completed"] = benchmark_completed(report)
    except Exception as error:
        report["errors"].append(str(error))
    finally:
        if process is not None and process.poll() is None:
            if xr_runtime and not (run / "xr-stop.request").exists():
                try:
                    report["xrShutdownRequested"] = True
                    report["xrShutdownAcknowledged"] = request_xr_shutdown(process, run)
                except OSError as error:
                    report["errors"].append("XR shutdown request failed: " + str(error))
            process.kill()
            process.wait(timeout=10)
            report["forcedTermination"] = True
        if game_running():
            report["errors"].append("Another game process remains: deployment retained for explicit recovery")
        else:
            try:
                if hardware_path and hardware_path.exists() and sha256(hardware_path) != sha256(hardware_input):
                    restore_xml_formatting(hardware_path, hardware_input, sha256(hardware_input),
                                           sha256(hardware_path), run / "hardware-settings-game-written.xml")
                conflicts = DiagnosticFiles.recover(run / "deployment.json", game, hardware_dir)
                report["recoveryConflicts"] = conflicts
                report["restored"] = not conflicts
            except Exception as error:
                report["restored"] = False
                report["errors"].append("Diagnostic recovery did not finish; originals retained: " + str(error))
        after = protected_state(game)
        formatting_restored = []
        if not game_running():
            for path, entry in originals.items():
                target = Path(path)
                if (path in after and after[path] != entry["sha256"]
                        and target.name in {"hardware_settings_config.xml", "hardware_settings_info.xml"}
                        and target.parent.name == "hardwaresettings"):
                    if restore_xml_formatting(target, backups / entry["backup"], entry["sha256"],
                                              after[path], run / (entry["backup"] + ".game-written.xml")):
                        formatting_restored.append(target.name)
            after = protected_state(game)
        report["formattingOnlyRestored"] = formatting_restored
        changed = [path for path in before.keys() | after.keys() if before.get(path) != after.get(path)]
        report["protectedFilesUnchanged"] = not changed
        # Detailed paths remain in the local receipt, not terminal output.
        save_json(run / "protected-comparison.json", {"before": before, "after": after, "changed": changed})
        if changed:
            report["errors"].append("Game changed protected state; originals and changed files retained for review")
        if report["errors"] or not report.get("restored"):
            report["completed"] = False
        save_json(run / "run.json", report)
        install_id = hashlib.sha256(str(game).upper().encode("utf-8")).hexdigest()[:24].upper()
        latest = Path(os.environ["LOCALAPPDATA"]) / "DiRT3VR" / install_id / "diagnostics/latest.json"
        latest.parent.mkdir(parents=True, exist_ok=True)
        save_json(latest, {"schema": 1, "kind": report["mode"], "directory": str(run), "report": report})
    print(json.dumps({**report, "directory": str(run)}, indent=2))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
