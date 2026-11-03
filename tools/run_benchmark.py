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
import zlib

import diagnostic_files
from diagnostic_files import (BLOOM_NAMES, DiagnosticFiles, FLOW_NAME, KEYBOARD_NAME, PATHS_NAME, SESSION_MAP, SESSION_MAP_NAME,
                              RESTRICTIONS_NAME, SEAT_NAME, SPECTATOR_NAME, preflight_replacement, no_links,
                              preflight_serial_render, replace_verified, restore_xml_formatting, save_json, sha256)

EXPECTED_EXE = "c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98"
ROOT = Path(__file__).resolve().parents[1]
REPLAY_PAIR_COUNT = 120
# The game's own MSAA levels (docs/research/graphics-options.md); VR sessions write "off" today (#c8076a1e).
MULTISAMPLING = ("off", "2xmsaa", "4xmsaa", "8xmsaa")
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
                           xr_runtime: Path | None = None, background_play: bool = False) -> dict:
    env = dict(os.environ, DIRT3VR_DIAGNOSTICS="1", DIRT3VR_LOG_DIR=str(directory), SteamAppId="321040")
    for flag in ("DIRT3VR_ENABLE_VR", "DIRT3VR_SCENE_DIAGNOSTICS", "DIRT3VR_REPLAY_DIAGNOSTICS",
                 "DIRT3VR_XR_PROTOTYPE", "DIRT3VR_XR_STOP_FILE", "XR_RUNTIME_JSON", "DIRT3VR_BACKGROUND_PLAY", "DIRT3VR_RESEARCH_PATCH", "DIRT3VR_RESEARCH_FILE_TRACE", "DIRT3VR_DIRECT_PRACTICE", "DIRT3VR_DIRECT_LAPS", "DIRT3VR_DIRECT_RETURN", "DIRT3VR_DIRECT_DIFFICULTY", "DIRT3VR_DIRECT_ASSISTS", "DIRT3VR_DIRECT_DEVICES", "DIRT3VR_RESEARCH_LAPS", "DIRT3VR_SCREENSHOT_SECONDS", "DIRT3VR_RESEARCH_SETTINGS_TRACE",
                "DIRT3VR_RESEARCH_STACKS", "DIRT3VR_RESEARCH_COUNT", "DIRT3VR_RESEARCH_PRESS_START_USER",
                "DIRT3VR_RESEARCH_AUTO_SAVE_CHOICE", "DIRT3VR_RESEARCH_STORE_TRACE",
                "DIRT3VR_RESEARCH_DATA_WATCH", "DIRT3VR_RESEARCH_GAME_LOG", "DIRT3VR_RESEARCH_VEHICLES", "DIRT3VR_RESEARCH_DEVICES", "DIRT3VR_RESEARCH_DEVICE_FIRST", "DIRT3VR_RESEARCH_LIVE_SEAT",
                "DIRT3VR_ANISOTROPY"):
        env.pop(flag, None)
    if background_play:
        env["DIRT3VR_BACKGROUND_PLAY"] = "1"
    if scene_observer:
        env["DIRT3VR_SCENE_DIAGNOSTICS"] = "1"
    if replay_pair:
        env["DIRT3VR_REPLAY_DIAGNOSTICS"] = "1"
    if xr_runtime is not None:
        env["DIRT3VR_XR_PROTOTYPE"] = "1"
        env["XR_RUNTIME_JSON"] = str(xr_runtime)
        env["DIRT3VR_XR_STOP_FILE"] = str(directory / "xr-stop.request")
    return env


def summarize_benchmark(path: Path, *, serial_render: bool = False, track: str = "aspen", route: str = "route_0",
                        cars_expected: tuple[str, ...] = ("frx",), multisampling: str = "off",
                        size: tuple[int, int] = (1280, 720)) -> dict:
    """Check game-produced results instead of treating a filename as acceptance."""
    root = ET.parse(path).getroot()
    tracks = root.findall("track")
    settings = root.find("track/settings")
    results = root.find("track/results")
    cars = settings.findall("car") if settings is not None else []
    confirmed = bool(root.tag == "benchmark" and len(tracks) == 1
                     and settings is not None and settings.get("name") == track
                     and settings.get("route") == route
                     and tuple(car.get("name") for car in cars) == tuple(cars_expected)
                     and results is not None and int(results.get("samples", "0")) > 0)
    hardware = root.find("hardware_settings_config")
    expected = {"graphics_card/directx": {"forcedx9": "false"},
                "graphics_card/stereo": {"enabled": "false"},
                "graphics_card/resolution": {"width": str(size[0]), "height": str(size[1]), "fullscreen": "false",
                                             "vsync": "0", "multisampling": multisampling}}
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


def diagnostic_hardware(data: bytes, *, serial_render: bool = False, multisampling: str = "off",
                        size: tuple[int, int] = (1280, 720)) -> bytes:
    """Build the unique diagnostic input without writing the user's settings file."""
    root = ET.fromstring(data)
    required = ["cpu/threadStrategy", "graphics_card/directx", "graphics_card/stereo", "graphics_card/resolution"]
    if root.tag != "hardware_settings_config" or any(len(root.findall(path)) != 1 for path in required):
        raise ValueError("Unexpected game hardware settings structure; no files changed")
    if serial_render:
        root.find("cpu/threadStrategy").set("parallelUpdateRender", "false")
    root.find("graphics_card/directx").set("forcedx9", "false")
    root.find("graphics_card/stereo").set("enabled", "false")
    if multisampling not in MULTISAMPLING:
        raise ValueError(f"Unsupported multisampling {multisampling!r}; no files changed")
    if len(size) != 2 or not all(isinstance(side, int) and 640 <= side <= 4096 for side in size):
        raise ValueError(f"Unsupported size {size!r}; no files changed")
    for key, value in {"width": str(size[0]), "height": str(size[1]), "fullscreen": "false", "vsync": "0", "multisampling": multisampling}.items():
        root.find("graphics_card/resolution").set(key, value)
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def benchmark_completed(report: dict) -> bool:
    summary = report.get("benchmarkSummary", {})
    return bool(report.get("supportedProxyLoaded") and report.get("lastLoggedPresentCount", 0) > 0
                and report.get("exitCode") == 0 and not report.get("forcedTermination")
                and report.get("benchmarkConfirmed")
                and (report.get("playerHardwareSettings") or summary.get("requestedConfigurationVerified"))
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


FOCUS_KEYS = {"esc": (0x01, False), "enter": (0x1C, False), "up": (0x48, True), "down": (0x50, True),
              "left": (0x4B, True), "right": (0x4D, True), "space": (0x39, False), "c": (0x2E, False)}  # c: Change View


def parse_focus_keys(text: str) -> list[tuple[float, str]]:
    """'40:esc,44:down,47:enter' -> ascending (seconds, key) pairs."""
    result = []
    for item in text.split(","):
        seconds, _, key = item.partition(":")
        try:
            value = float(seconds)
        except ValueError:
            raise argparse.ArgumentTypeError("use SECONDS:KEY[,SECONDS:KEY...], e.g. 40:esc,44:down") from None
        if not 1 <= value <= 3600 or key not in FOCUS_KEYS or (result and value <= result[-1][0]) or len(result) == 32:
            raise argparse.ArgumentTypeError(f"ascending SECONDS:KEY pairs (1-3600 s, at most 32) with keys {', '.join(FOCUS_KEYS)}")
        result.append((value, key))
    return result


class _KeyboardInput(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class _Input(ctypes.Structure):
    class _Union(ctypes.Union):
        _fields_ = [("ki", _KeyboardInput), ("padding", ctypes.c_byte * 32)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _Union)]


def press_in_focused_game(pid: int, key: str) -> str:
    """Bring the game forward and press one key, but only while the game really is the foreground window."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetForegroundWindow.restype = wintypes.HWND
    windows: list[int] = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def collect(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            windows.append(hwnd)
        return True

    def foreground_is_game() -> bool:
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), ctypes.byref(owner))
        return owner.value == pid

    user32.EnumWindows(callback_type(collect), 0)
    if not windows:
        return "no-window"
    if not foreground_is_game():
        # Windows only lets the foreground owner hand over focus; borrow its input queue briefly.
        kernel32 = ctypes.WinDLL("kernel32")
        foreground_thread = user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), None)
        own_thread = kernel32.GetCurrentThreadId()
        attached = bool(foreground_thread) and foreground_thread != own_thread and \
            bool(user32.AttachThreadInput(own_thread, foreground_thread, True))
        try:
            user32.ShowWindow(windows[0], 9)  # SW_RESTORE
            user32.BringWindowToTop(windows[0])
            user32.SetForegroundWindow(windows[0])
        finally:
            if attached:
                user32.AttachThreadInput(own_thread, foreground_thread, False)
        time.sleep(0.3)
    if not foreground_is_game():
        return "not-foreground"  # never type into another program
    scan, extended = FOCUS_KEYS[key]
    flags = 0x0008 | (0x0001 if extended else 0)  # KEYEVENTF_SCANCODE (+ EXTENDEDKEY)
    for up in (False, True):
        event = _Input(type=1)
        event.ki = _KeyboardInput(0, scan, flags | (0x0002 if up else 0), 0, 0)
        user32.SendInput(1, ctypes.byref(event), ctypes.sizeof(_Input))
        time.sleep(0.1)
    return "sent"


def bmp_to_png(source: Path) -> Path:
    """Convert the renderer's 32-bit top-down BMP screenshot to an RGB PNG next to it."""
    data = source.read_bytes()
    if len(data) < 54 or data[:2] != b"BM":
        raise ValueError(f"Not a screenshot BMP: {source.name}")
    offset, width, height, bits = (struct.unpack_from("<I", data, 10)[0], struct.unpack_from("<i", data, 18)[0],
                                   struct.unpack_from("<i", data, 22)[0], struct.unpack_from("<H", data, 28)[0])
    if bits != 32 or width <= 0 or height >= 0 or len(data) != offset + width * -height * 4:
        raise ValueError(f"Unexpected screenshot layout: {source.name}")
    height = -height
    pixels = data[offset:]
    rgb = bytearray(width * height * 3)
    rgb[0::3], rgb[1::3], rgb[2::3] = pixels[2::4], pixels[1::4], pixels[0::4]
    stride = width * 3
    raw = b"".join(b"\x00" + rgb[y * stride:(y + 1) * stride] for y in range(height))

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    target = source.with_suffix(".png")
    target.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                       + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))
    return target


CATALOG = ROOT / "tools/data/event-catalog.json"


def parse_seat_offset(text: str) -> tuple[float, float, float]:
    try:
        values = tuple(float(part) for part in text.split(","))
    except ValueError:
        raise argparse.ArgumentTypeError("use SIDE,UP,FORWARD in metres, e.g. 0,0.2,0") from None
    if len(values) != 3 or any(abs(v) > 0.5 for v in values):
        raise argparse.ArgumentTypeError("three offsets within ±0.5 m")
    return values


def listed_sizes(info: bytes) -> set[tuple[int, int]]:
    """The display sizes in the game's hardware_settings_info.xml. DiRT 3 runs any other size at 1280x720 and
    writes that back, which leaves the run's settings copy changed and its recovery refused."""
    sizes = set()
    for element in ET.fromstring(info).iter("resolution"):
        try:
            sizes.add((int(element.get("width", "")), int(element.get("height", ""))))
        except ValueError:
            continue
    return sizes


def parse_size(text: str) -> tuple[int, int]:
    try:
        width, height = (int(part) for part in text.lower().split("x"))
    except ValueError:
        raise argparse.ArgumentTypeError("use WIDTHxHEIGHT, e.g. 3072x3264") from None
    if not (640 <= width <= 4096 and 640 <= height <= 4096):
        raise argparse.ArgumentTypeError("each side between 640 and 4096")
    return width, height


def choose_scene(route_text: str | None, car: str | None, opponents: int) -> dict:
    """Pick the benchmark track and cars from the event catalog (default: Aspen route_0 with frx, no opponents)."""
    if route_text is None and car is None and not opponents:
        return {"country": "usa", "track": "aspen", "route": "route_0", "discipline": "rallycross", "cars": ["frx"]}
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    country, track, route = (route_text or "usa/aspen/route_0").split("/") if (route_text or "").count("/") in (0, 2) else (None, None, None)
    matches = [r for r in catalog["routes"] if (r["country"], r["track"], r["route"]) == (country, track, route)]
    if len(matches) != 1:
        raise ValueError("--route must be COUNTRY/TRACK/ROUTE from tools/data/event-catalog.json")
    discipline = matches[0]["discipline"]
    cars = {c["code"]: c for c in catalog["cars"]}
    player = car or ("frx" if route_text is None else next(c["code"] for c in catalog["cars"] if c["discipline"] == discipline))
    if player not in cars:
        raise ValueError("--car must be a car code from tools/data/event-catalog.json")
    others = [c["code"] for c in catalog["cars"] if c["discipline"] == discipline and c["code"] != player]
    if not 0 <= opponents <= min(7, len(others)):
        raise ValueError(f"--opponents must be 0-{min(7, len(others))} for {discipline}")
    return {"country": country, "track": track, "route": route, "discipline": discipline,
            "cars": [player] + others[:opponents]}


def restore_player_hardware(target: Path, backup: Path, original_hash: str, retained: Path) -> bool:
    """Put the player's settings file back byte-exact after the game rewrote it, keeping the game's copy."""
    for path in (target, backup, retained):
        no_links(path)
    original = backup.read_bytes()
    if hashlib.sha256(original).hexdigest() != original_hash:
        raise ValueError("Backup of the player's hardware settings changed; current file preserved")
    if not target.is_file():
        raise FileNotFoundError("The player's hardware settings file disappeared; backup retained")
    current = target.read_bytes()
    current_hash = hashlib.sha256(current).hexdigest()
    if current_hash == original_hash:
        return False
    with retained.open("xb") as stream:
        stream.write(current)
    replace_verified(target, original, current_hash)
    return True


def protected_state(game: Path) -> dict[str, str]:
    paths = [game / "dirt3_game.exe", game / "steam_api.dll",
             game / RESTRICTIONS_NAME, game / SPECTATOR_NAME, game / SEAT_NAME, game / FLOW_NAME,
             game / KEYBOARD_NAME, game / PATHS_NAME, *(game / name for name in BLOOM_NAMES),
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
    parser.add_argument("--background-play", action="store_true",
                        help="Keep the game running when it loses focus (suppresses its focus-loss pause in memory) and start its window without activating it")
    parser.add_argument("--direct-practice", action="store_true",
                        help="Keep the benchmark's car under player control (the renderer's direct-practice patch); no input is sent, so the car stays at the start and no result is written")
    parser.add_argument("--spectator-human-switch", action="store_true",
                        help="Research: temporarily add the normal race's 'Controller / Switch type=human' event to cutscene/spectator_startsequence.xml (journaled and restored)")
    parser.add_argument("--no-bloom", action="store_true",
                        help="Research: zero the BloomFromDsX4 mixes of postprocess/effects.xml and tracks/generic/(night_)effects.xml (tools/bloom.py; journaled and restored)")
    parser.add_argument("--anisotropy", type=int, choices=(0, 1, 2, 4, 8, 16),
                        help="Anisotropic filtering level for the game's samplers (DIRT3VR_ANISOTROPY; 1 = trilinear); unset leaves them as the game made them")
    parser.add_argument("--msaa", choices=MULTISAMPLING, default="off",
                        help="The game's MSAA (graphics_card/resolution@multisampling) for this run; default off, as VR sessions write it")
    parser.add_argument("--size", type=parse_size, default=(1280, 720), metavar="WIDTHxHEIGHT",
                        help="The game's windowed size for this run, e.g. 3072x3264 for a headset's pixel count; default 1280x720")
    parser.add_argument("--research-patch", metavar="RVA:ORIGINAL:REPLACEMENT",
                        help="Research only: in-memory code change applied by the research proxy (build/native/bin/research/d3d11.dll, pass it with --proxy) after its original bytes match")
    parser.add_argument("--screenshots", metavar="SECONDS",
                        help="Comma-separated, ascending seconds after the first frame (e.g. 40,100,160): the renderer saves the "
                             "game's frame at each, converted to screenshot-<n>-<s>s.png in the run directory")
    parser.add_argument("--route", metavar="COUNTRY/TRACK/ROUTE",
                        help="Start on this catalog route instead of usa/aspen/route_0")
    parser.add_argument("--car", metavar="CODE", help="The (first) car, a catalog code; default frx, or the route discipline's first car")
    parser.add_argument("--opponents", type=int, default=0, metavar="N",
                        help="Add N more cars (0-7) of the route's discipline from the catalog")
    parser.add_argument("--load-profile", nargs="?", const="full", choices=("signin", "dataset", "begin", "reset", "list", "bare", "cleanup", "full"),
                        help="Research: journaled system/flow.bin change that makes the benchmark start load the player's "
                             "profile (skipfe normally skips it)")
    parser.add_argument("--seat-test", type=parse_seat_offset, metavar="SIDE,UP,FORWARD",
                        help="Research: journaled change of cars/models/frx/cameras.xml that starts the car in its cockpit "
                             "view with the eye point moved by these metres (0,0,0 = cockpit start only)")
    parser.add_argument("--seat-cockpit", choices=("hide", "first"), default="hide",
                        help="With --seat-test: hide the views before head-cam (the default), or move head-cam to the front of the "
                             "view list and hide nothing (seat_cameras.cockpit_first); a --recover needs the same choice")
    parser.add_argument("--player-copies", type=int, choices=range(1, 9), default=1, metavar="K",
                        help="number= on the first <car>: the player's car plus K-1 AI copies of it")
    parser.add_argument("--direct-difficulty", type=int, choices=range(0, 6), metavar="N",
                        help="AI difficulty level 0-5 for the benchmark start (renderer, DIRT3VR_DIRECT_DIFFICULTY)")
    parser.add_argument("--direct-assists", metavar="NAME=VALUE,...",
                        help="assists/gearbox for the benchmark start, e.g. assists_abs=0,transmission_type=1 "
                             "(renderer, DIRT3VR_DIRECT_ASSISTS)")
    parser.add_argument("--direct-devices", metavar="NAME|NAME",
                        help="devices to bind to player 1 in the benchmark start, e.g. xinput_pad "
                             "(renderer, DIRT3VR_DIRECT_DEVICES)")
    parser.add_argument("--direct-return", action="store_true",
                        help="The renderer's return-to-menus change (DIRT3VR_DIRECT_RETURN): after the finish DiRT 3 goes to its "
                             "menus instead of quitting; implies --stay-after-result")
    parser.add_argument("--stay-after-result", action="store_true",
                        help="Research: keep the game running after its benchmark result appears (until it exits or --seconds)")
    parser.add_argument("--focus-keys", type=parse_focus_keys, metavar="SECONDS:KEY,...",
                        help="Research, intrusive: brings DiRT 3 to the front and presses real keys (scan codes) at the given "
                             "seconds after start; keys are only sent while the game is the foreground window")
    parser.add_argument("--direct-laps", type=int, choices=range(1, 21), metavar="N",
                        help="With --direct-practice: the renderer sets the race to N laps (DIRT3VR_DIRECT_LAPS)")
    parser.add_argument("--research-laps", type=int, choices=range(1, 21), metavar="N",
                        help="Research only: the research proxy sets the benchmark's lap count to N")
    parser.add_argument("--player-hardware-settings", action="store_true",
                        help="Point the benchmark at the player's own hardware_settings_config.xml (as direct launch will) instead of a "
                             "temporary windowed copy; the game runs at the player's settings and the file is put back byte-exact "
                             "afterwards, keeping the game-written copy")
    parser.add_argument("--normal-start", action="store_true",
                        help="Research: start DiRT 3 normally (logos, Press Start, menus) instead of -benchmark; no result is expected")
    parser.add_argument("--stacks", metavar="SECONDS",
                        help="Research only: the research proxy logs every thread's call stack at these seconds, "
                             "e.g. 40,50")
    parser.add_argument("--actionmap-test", choices=("edit", "add"),
                        help="Research: bind Pause to Space through a journaled change to the stock keyboard map "
                             "(edit) or an added keyboard map listed in actionMapPaths.xml (add)")
    parser.add_argument("--data-watch", action="store_true",
                        help="Research only: log the auto_save / hasEverSaved flags (they gate profile saves) when read")
    parser.add_argument("--store-trace", action="store_true",
                        help="Research only: log each value the game reads from its settings store (assists, difficulty)")
    parser.add_argument("--auto-save-choice", action="store_true",
                        help="Research only: when the profile load asks for a save to be chosen, choose the first one "
                             "as the save list's confirm would")
    parser.add_argument("--log-profile-wait", action="store_true",
                        help="Research only: log wait_for_load_profile's operation status and links without changing them")
    parser.add_argument("--press-start-user", type=int, choices=range(4), metavar="N",
                        help="Research only: before the sign-in state, set the user index Press Start would have "
                             "set (press_start_user_index and the input component)")
    parser.add_argument("--count-calls", metavar="ADDRESSES",
                        help="Research only: count calls to up to 8 functions (hex addresses at base 0x400000, "
                             "comma-separated), logged at the --stacks seconds instead of stacks")
    parser.add_argument("--settings-trace", action="store_true",
                        help="Research only: the research proxy logs each profile setting the game looks up (name=value)")
    parser.add_argument("--file-trace", action="store_true",
                        help="Research only: the research proxy logs every file the game opens after device creation")
    parser.add_argument("--game-log", action="store_true",
                        help="Research only: the research proxy logs the game's own log (e.g. the race load's vehicle list)")
    parser.add_argument("--vehicles", metavar="SECONDS",
                        help="Research only: the research proxy logs the race's vehicle name table at these seconds, e.g. 40,90")
    parser.add_argument("--live-seat", metavar="S:y=M,S:fov=D",
                        help="Research only: add to the selected head camera's eye height or FOV at these seconds, e.g. 60:y=0.3,90:fov=15")
    parser.add_argument("--devices", action="store_true",
                        help="Research only: the research proxy logs the input device lists and binds of a direct start")
    parser.add_argument("--device-first", metavar="FAMILY",
                        help="Research only (with --devices): move this device family to the front of the direct start's list")
    parser.add_argument("--recover", type=Path, metavar="JOURNAL")
    args = parser.parse_args()
    if args.replay_pair and not (args.serial_render and args.scene_observer):
        parser.error("--replay-pair requires both --serial-render and --scene-observer")
    if args.xr_runtime and (not (args.serial_render and args.scene_observer) or args.replay_pair):
        parser.error("--xr-runtime requires --serial-render and --scene-observer and cannot be combined with --replay-pair")
    if args.screenshots is not None:
        times = args.screenshots.split(",")
        if (not all(t.isdigit() for t in times) or len(times) > 16
                or any(not 1 <= int(t) <= 3600 for t in times)
                or any(int(a) >= int(b) for a, b in zip(times, times[1:]))):
            parser.error("--screenshots needs up to 16 ascending whole seconds between 1 and 3600, e.g. 40,100,160")
    try:
        scene = choose_scene(args.route, args.car, args.opponents)
    except (ValueError, StopIteration) as error:
        parser.error(str(error) or "no car for that route's discipline")
    if args.direct_laps and not args.direct_practice:
        parser.error("--direct-laps requires --direct-practice")
    if args.size != (1280, 720) and args.replay_pair:
        parser.error("--replay-pair checks 1280x720 eye images; use it without --size")
    if args.player_hardware_settings and args.serial_render:
        parser.error("--player-hardware-settings cannot be combined with --serial-render, which needs a modified copy")
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
    if args.size != (1280, 720):
        info = hardware_dir / "hardware_settings_info.xml"
        no_links(info)
        if args.size not in listed_sizes(info.read_bytes()):
            raise ValueError(f"DiRT 3 doesn't list {args.size[0]}x{args.size[1]} in hardware_settings_info.xml; no files changed")
    hardware_bytes = diagnostic_hardware(hardware_source.read_bytes(), serial_render=args.serial_render, multisampling=args.msaa,
                                         size=args.size)
    restrictions = preflight_serial_render(game) if args.serial_render else None
    xr_runtime = preflight_xr_manifest(args.xr_runtime) if args.xr_runtime else None
    spectator = preflight_replacement(game, SPECTATOR_NAME) if args.spectator_human_switch else None
    no_bloom = [(name, preflight_replacement(game, name)[0]) for name in BLOOM_NAMES] if args.no_bloom else []
    if args.seat_test is not None:
        if "frx" not in scene["cars"][:1]:
            parser.error("--seat-test changes the frx camera file; use the frx as the (first) car")
        diagnostic_files.SEAT_TEST_OFFSET = args.seat_test
        diagnostic_files.SEAT_TEST_COCKPIT = args.seat_cockpit
    seat = preflight_replacement(game, SEAT_NAME) if args.seat_test is not None else None
    if args.load_profile:
        diagnostic_files.PROFILE_LOAD_STEPS = args.load_profile
    profile_flow = preflight_replacement(game, FLOW_NAME) if args.load_profile else None
    keyboard_map = preflight_replacement(game, KEYBOARD_NAME) if args.actionmap_test == "edit" else None
    map_paths = preflight_replacement(game, PATHS_NAME) if args.actionmap_test == "add" else None
    if args.actionmap_test == "add":
        no_links(game / SESSION_MAP_NAME)
        if (game / SESSION_MAP_NAME).exists():
            raise FileExistsError(f"{SESSION_MAP_NAME} already exists; nothing changed")
    preflight = {"gameSha256": EXPECTED_EXE, "proxySha256": hashlib.sha256(proxy_bytes).hexdigest(),
                 "mode": "headset-prototype" if xr_runtime else "desktop-eye-replay" if args.replay_pair else "desktop-observation",
                 "stereo": False, "sceneObserver": args.scene_observer,
                 "serialRender": args.serial_render, "replayPair": args.replay_pair,
                 "xrRuntime": xr_runtime, "backgroundPlay": args.background_play, "researchPatch": args.research_patch,
                 "spectatorHumanSwitch": args.spectator_human_switch, "noBloom": args.no_bloom, "seatCockpit": args.seat_cockpit if args.seat_test is not None else None,"anisotropy": args.anisotropy, "multisampling": args.msaa, "size": list(args.size), "directPractice": args.direct_practice,
                 "playerHardwareSettings": args.player_hardware_settings, "directLaps": args.direct_laps, "directReturn": args.direct_return, "scene": scene,
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
    if args.player_hardware_settings and str(hardware_source) not in originals:
        raise RuntimeError("The player's hardware settings file was not backed up; nothing deployed")
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
        if spectator:
            transaction.apply_replacement(SPECTATOR_NAME, spectator[0])
        for name, original in no_bloom:
            transaction.apply_replacement(name, original)
        if seat:
            transaction.apply_replacement(SEAT_NAME, seat[0])
        if profile_flow:
            transaction.apply_replacement(FLOW_NAME, profile_flow[0])
        if keyboard_map:
            transaction.apply_replacement(KEYBOARD_NAME, keyboard_map[0])
        if map_paths:
            # The map file first, so the list never names a missing file.
            transaction.add(SESSION_MAP_NAME, SESSION_MAP)
            transaction.apply_replacement(PATHS_NAME, map_paths[0])
        if args.player_hardware_settings:
            # The game reads and rewrites this file; its original is in protected-originals.
            hardware_name = hardware_source.name
        else:
            hardware_name = f"dirt3vr-hardware-{run_id}.xml"
            hardware_input.write_bytes(hardware_bytes)
            hardware_path = transaction.add(hardware_name, hardware_input.read_bytes())
        benchmark = ET.Element("config", {"skipreplays": "true", "outputfilename": f"DiRT3VR_{run_id}",
                                           "hardwaresettings": hardware_name})
        track = ET.SubElement(benchmark, "track", {"country": scene["country"], "name": scene["track"], "route": scene["route"]})
        for index, code in enumerate(scene["cars"]):
            # "number" is how many copies of this car race (the game's example_benchmark.xml), not a grid slot.
            ET.SubElement(track, "car", {"name": code, "number": str(args.player_copies if index == 0 else 1)})
        benchmark_path = transaction.add(f"dirt3vr-benchmark-{run_id}.xml", ET.tostring(benchmark, encoding="utf-8", xml_declaration=True))
        transaction.add("d3d11.dll", proxy_bytes)
        env = diagnostic_environment(run, scene_observer=args.scene_observer, replay_pair=args.replay_pair,
                                     xr_runtime=args.xr_runtime, background_play=args.background_play)
        if args.research_patch:
            env["DIRT3VR_RESEARCH_PATCH"] = args.research_patch
        if args.anisotropy is not None:
            env["DIRT3VR_ANISOTROPY"] = str(args.anisotropy)
        if args.file_trace:
            env["DIRT3VR_RESEARCH_FILE_TRACE"] = "1"
        if args.game_log:
            env["DIRT3VR_RESEARCH_GAME_LOG"] = "1"
        if args.vehicles:
            env["DIRT3VR_RESEARCH_VEHICLES"] = args.vehicles
        if args.live_seat:
            env["DIRT3VR_RESEARCH_LIVE_SEAT"] = args.live_seat
        if args.devices:
            env["DIRT3VR_RESEARCH_DEVICES"] = "1"
            if args.device_first:
                env["DIRT3VR_RESEARCH_DEVICE_FIRST"] = args.device_first
        if args.settings_trace:
            env["DIRT3VR_RESEARCH_SETTINGS_TRACE"] = "1"
        if args.stacks:
            env["DIRT3VR_RESEARCH_STACKS"] = args.stacks
        if args.store_trace:
            env["DIRT3VR_RESEARCH_STORE_TRACE"] = "1"
        if args.data_watch:
            env["DIRT3VR_RESEARCH_DATA_WATCH"] = "1"
        if args.auto_save_choice:
            env["DIRT3VR_RESEARCH_AUTO_SAVE_CHOICE"] = "1"
        elif args.log_profile_wait:
            env["DIRT3VR_RESEARCH_AUTO_SAVE_CHOICE"] = "2"
        if args.press_start_user is not None:
            env["DIRT3VR_RESEARCH_PRESS_START_USER"] = str(args.press_start_user)
        if args.count_calls:
            env["DIRT3VR_RESEARCH_COUNT"] = args.count_calls
        if args.direct_practice:
            env["DIRT3VR_DIRECT_PRACTICE"] = "1"
        if args.direct_laps:
            env["DIRT3VR_DIRECT_LAPS"] = str(args.direct_laps)
        if args.direct_return:
            env["DIRT3VR_DIRECT_RETURN"] = "1"
        if args.direct_difficulty is not None:
            env["DIRT3VR_DIRECT_DIFFICULTY"] = str(args.direct_difficulty)
        if args.direct_assists:
            env["DIRT3VR_DIRECT_ASSISTS"] = args.direct_assists
        if args.direct_devices:
            env["DIRT3VR_DIRECT_DEVICES"] = args.direct_devices
        if args.screenshots:
            env["DIRT3VR_SCREENSHOT_SECONDS"] = args.screenshots
        if args.research_laps:
            env["DIRT3VR_RESEARCH_LAPS"] = str(args.research_laps)
        startup = None
        if args.background_play:
            # Ask Windows not to activate the game window; DiRT 3 may still bring itself forward.
            startup = subprocess.STARTUPINFO(dwFlags=subprocess.STARTF_USESHOWWINDOW, wShowWindow=4)  # SW_SHOWNOACTIVATE
        started = time.monotonic()
        command = [str(exe)] if args.normal_start else [str(exe), "-benchmark", benchmark_path.name]
        process = subprocess.Popen(command, cwd=game, env=env, startupinfo=startup)
        report["pid"] = process.pid
        save_json(run / "run.json", report)
        print(f"Diagnostic running: pid={process.pid}; log directory={run}", flush=True)
        result_seen_at = None
        pending_keys = list(args.focus_keys or [])
        if args.focus_keys:
            report["focusKeys"] = []
        while process.poll() is None and time.monotonic() - started < args.seconds:
            time.sleep(0.2 if pending_keys else 1)
            while pending_keys and time.monotonic() - started >= pending_keys[0][0]:
                _, key = pending_keys.pop(0)
                report["focusKeys"].append({"key": key, "atSeconds": round(time.monotonic() - started, 2),
                                            "result": press_in_focused_game(process.pid, key)})
            if (xr_runtime and not report.get("xrShutdownRequested")
                    and time.monotonic() - started >= min(90, args.seconds - 10)):
                # The benchmark may exit naturally. Stop XR while its render thread
                # is still active, then leave the desktop benchmark to finish.
                report["xrShutdownRequested"] = True
                report["xrShutdownAtSeconds"] = round(time.monotonic() - started, 2)
                report["xrShutdownAcknowledged"] = request_xr_shutdown(process, run)
            if benchmark_results.exists() and any(benchmark_results.glob(f"DiRT3VR_{run_id}*")):
                result_seen_at = result_seen_at or time.monotonic()
                if time.monotonic() - result_seen_at >= 5 and not (args.stay_after_result or args.direct_return):
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
        if args.direct_practice:
            report["directPracticeInstalled"] = "Direct practice: benchmark start keeps the player's car under player control" in logs
            if not report["directPracticeInstalled"]:
                report["errors"].append("Direct practice was requested but the proxy did not install it")
        if args.screenshots:
            report["screenshots"] = []
            for bmp in sorted(run.glob("screenshot-*.bmp")):
                try:
                    report["screenshots"].append(bmp_to_png(bmp).name)
                    bmp.unlink()
                except (OSError, ValueError) as error:
                    report["errors"].append("Screenshot conversion failed: " + str(error))
        if args.direct_difficulty is not None:
            report["directDifficultyHooked"] = "Direct practice difficulty: AI level" in logs
            used = re.findall(r"AI level (\d) instead of the profile's (-?\d+)", logs)
            report["directDifficultyAnswers"] = [{"level": int(a), "profile": int(b)} for a, b in used]
            if not report["directDifficultyHooked"]:
                report["errors"].append("AI difficulty was requested but the renderer did not hook it")
        if args.direct_assists:
            report["directAssistsHooked"] = "Direct practice assists: " in logs and "during the benchmark start" in logs
            report["directAssistsAnswers"] = re.findall(r"Direct practice assists: (\w+) (\d) instead of (-?\d+)", logs)
            if not report["directAssistsHooked"]:
                report["errors"].append("Assists were requested but the renderer did not hook them")
        if args.direct_return:
            report["directReturnInstalled"] = "Direct practice return: the finish leads back to DiRT 3's menus" in logs
            report["directReturnBenchmarkEnded"] = "benchmark mode off, heading to DiRT 3's menus" in logs
            if not report["directReturnInstalled"]:
                report["errors"].append("Return to menus was requested but the renderer did not install it")
        if args.direct_laps:
            report["directLapsWritten"] = f"Direct practice laps: benchmark count 0 -> {args.direct_laps}" in logs
            if not report["directLapsWritten"]:
                report["errors"].append("Direct practice laps were requested but the renderer did not set them")
        if args.research_patch:
            report["researchPatchesApplied"] = len(re.findall(r"Research patch applied", logs))
            report["researchPatchRefused"] = "Research patch refused" in logs
        if args.background_play:
            report["backgroundPlayInstalled"] = "Background play: focus-loss pause request and replay pause suppressed" in logs
            report["presentLogsInBackground"] = len(re.findall(r"Present count=\d+ .* foreground=0", logs))
            report["presentLogsInForeground"] = len(re.findall(r"Present count=\d+ .* foreground=1", logs))
            if not report["backgroundPlayInstalled"]:
                report["errors"].append("Background play was requested but the proxy did not install it")
        counts = [int(x) for x in re.findall(r"Present count=(\d+)", logs)]
        report["lastLoggedPresentCount"] = max(counts, default=0)
        report["requestedSwapchainObserved"] = bool(re.search(rf"Swapchain.*size={args.size[0]}x{args.size[1]}\b", logs))
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
                kept = run / result.name
                kept.write_bytes(result.read_bytes())
                summary = summarize_benchmark(kept, serial_render=args.serial_render, track=scene["track"],
                                              route=scene["route"], cars_expected=tuple(scene["cars"]), multisampling=args.msaa,
                                              size=args.size)
                # The result is this run's own file (its run id is in the name); keep only the copy.
                if kept.read_bytes() == result.read_bytes():
                    result.unlink()
                report["benchmarkSummary"] = summary
                report["benchmarkConfirmed"] = summary["sceneConfirmed"]
                report["serialRenderVerified"] = summary["serialRenderVerified"]
                # With the player's own file the game runs at the player's settings, not the diagnostic copy.
                if not summary["requestedConfigurationVerified"] and not args.player_hardware_settings:
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
                if args.player_hardware_settings:
                    report["playerHardwareRewritten"] = restore_player_hardware(
                        hardware_source, backups / originals[str(hardware_source)]["backup"],
                        originals[str(hardware_source)]["sha256"], run / "player-hardware-settings-game-written.xml")
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
