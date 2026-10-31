"""Reproduce the static DiRT 3 render-boundary evidence; never executes the game.

Uses only Python's standard library plus GNU objdump when --objdump is supplied.
Addresses below belong to one fingerprint and are research candidates, not an
authorization to install hooks or replay the renderer.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess


EXPECTED_SHA256 = "c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98"
RANGES = {
    "inner_scene": (0x984A50, 0x984B3E),
    "outer_scene": (0x98A270, 0x98A447),
    "main_scene_caller": (0x580540, 0x580667),
    "main_renderer_constructor": (0x58AC30, 0x58AF40),
    "camera_constructor": (0x47BFE0, 0x47C0B1),
    "camera_setup": (0x97A9B0, 0x97B64A),
    "list_header_constructor": (0x96E9A0, 0x96EC3E),
    "main_colour_dispatch": (0x5804C0, 0x580534),
    "colour_render_counter": (0x96E630, 0x96E7F6),
    "native_eye_switch": (0x8A8C60, 0x8A8CA0),
    "native_driver_stereo_state": (0x8A8CA0, 0x8A8CDD),
    "late_list_header_writer": (0xC16C20, 0xC16D26),
    "list_aggregate_update": (0x8C67D0, 0x8C6808),
    "main_prepass_header_writer_call": (0x580FB8, 0x580FE3),
    "main_colour_header_writer_call": (0x581467, 0x58149D),
    "benchmark_command_parser": (0x6AA900, 0x6AAB2F),
    "benchmark_xml_reader": (0x6A8800, 0x6A890A),
    "benchmark_config_values": (0x6A7850, 0x6A79A4),
    "benchmark_result_directory": (0x6A7350, 0x6A73CC),
    "benchmark_result_writer": (0x6A73D0, 0x6A784A),
    "benchmark_track_completion": (0x6A8720, 0x6A87F2),
    "user_documents_root": (0x624CD0, 0x624DA4),
    "hardware_virtual_mount": (0xB2E1A0, 0xB2E3F7),
    "hardware_active_directory_mount": (0xB32270, 0xB322E7),
    "hardware_file_reader": (0xB2E400, 0xB2E58C),
    "hardware_selected_file_fallback": (0xB322F0, 0xB32377),
    "hardware_system_file_fallback": (0xB3244A, 0xB32474),
    "parallel_update_render_consumer": (0x584B90, 0x584EE1),
    "xml_boolean_reader": (0xCD2CF0, 0xCD2D30),
    "main_prepared_finalize": (0x580A80, 0x580C95),
    "inner_depth_stage": (0x96E410, 0x96E48D),
    "outer_shadow_prepare": (0x96E490, 0x96E509),
    "inner_shadow_stage": (0x96E510, 0x96E5A6),
    "late_list_generate": (0xC16BA0, 0xC16C1F),
    "list_sort_and_publish": (0x8C6510, 0x8C67D0),
    "tree_list_flatten": (0x8A33D0, 0x8A3435),
    "tree_list_publish": (0x8A3440, 0x8A346F),
    "velocity_depth_factory": (0x733030, 0x7330D7),
    "velocity_depth_constructor": (0x732A70, 0x732C46),
    "velocity_depth_setup": (0x72C9C0, 0x72D220),
    "velocity_history_invalidate": (0x732C50, 0x732C58),
    "velocity_eye_select": (0x732D90, 0x732DA0),
    "velocity_history_get": (0x71BB00, 0x71BB37),
    "camera_node_factory": (0x8E2A50, 0x8E2AAE),
    "camera_node_constructor": (0x8E1550, 0x8E15E8),
    "camera_node_projection_builder": (0x8AB360, 0x8AB672),
    "camera_upload": (0x8A81F0, 0x8A8921),
    "d3d11_device_constructor_prefix": (0x8D8BD0, 0x8D8CE9),
    "main_foreground_camera_pass": (0x5806C0, 0x580820),
    "main_visibility_prepare": (0x5808B0, 0x580A73),
    "frustum_build": (0x7C55D0, 0x7C5C39),
    "frustum_copy": (0x574C10, 0x574CAE),
    "main_prepass_late_list_consumer": (0x580FB8, 0x580FFD),
    "main_colour_late_list_consumer": (0x581467, 0x5814C8),
    "velocity_depth_list_draw": (0x71BA00, 0x71BA2F),
}
GUARDS = {
    "inner_scene": "55 8b ec 56 8b f1 80 7e 55 00 74 16 8b 4e 38 85",
    "outer_scene": "53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04",
    "velocity_eye_select": "55 8b ec 8b 45 08 89 81 a0 00 00 00 5d c2 04 00",
    "velocity_history_invalidate": "c6 81 90 00 00 00 00 c3",
    "velocity_depth_setup": "53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04",
    "camera_setup": "53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04",
    "camera_upload": "53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04",
    "frustum_build": "53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04",
    "frustum_copy": "55 8b ec 8b c1 8b 4d 08 0f 28 01 0f 29 00 0f 28",
}


class Pe32:
    def __init__(self, path: Path):
        self.path = path.resolve(strict=True)
        self.data = self.path.read_bytes()
        self.sha256 = hashlib.sha256(self.data).hexdigest()
        if self.sha256 != EXPECTED_SHA256:
            raise ValueError("Unsupported executable SHA-256; no address claims are valid")
        pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[:2] != b"MZ" or self.data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("Not a PE executable")
        machine, count = struct.unpack_from("<HH", self.data, pe + 4)
        opt_size = struct.unpack_from("<H", self.data, pe + 20)[0]
        magic = struct.unpack_from("<H", self.data, pe + 24)[0]
        if machine != 0x14C or magic != 0x10B:
            raise ValueError("Expected an x86 PE32 image")
        self.base = struct.unpack_from("<I", self.data, pe + 52)[0]
        self.sections = []
        for i in range(count):
            entry = pe + 24 + opt_size + i * 40
            name = self.data[entry:entry + 8].split(b"\0")[0].decode("ascii")
            virtual_size, rva, size, raw = struct.unpack_from("<IIII", self.data, entry + 8)
            self.sections.append(dict(name=name, rva=rva, virtual_size=virtual_size,
                                      raw_offset=raw, raw_size=size))

    def read_va(self, address: int, count: int) -> tuple[int, bytes]:
        rva = address - self.base
        for section in self.sections:
            delta = rva - section["rva"]
            if 0 <= delta and delta + count <= section["raw_size"]:
                offset = section["raw_offset"] + delta
                return offset, self.data[offset:offset + count]
        raise ValueError(f"Unmapped file-backed range: {address:#x}+{count:#x}")


def inspect(path: Path, output: Path, objdump: str | None) -> None:
    pe = Pe32(path)
    output.mkdir(parents=True, exist_ok=True)
    report = {
        "target": str(pe.path), "sha256": pe.sha256,
        "image_base": hex(pe.base), "evidence_kind": "static_file_bytes",
        "limitations": ["No process execution or live observation",
                        "Static native stereo branch is not proof of safe OpenXR replay",
                        "Called functions, GPU resources, and intrusive list nodes can mutate"],
        "ranges": {}, "tables": {}, "calls": {}, "strings": {},
    }
    for name, (start, end) in RANGES.items():
        raw, data = pe.read_va(start, end - start)
        guard = bytes.fromhex(GUARDS[name]) if name in GUARDS else None
        if guard is not None and not data.startswith(guard):
            raise ValueError(f"Guard mismatch: {name}")
        report["ranges"][name] = {
            "va": hex(start), "rva": hex(start - pe.base),
            "end_va_exclusive": hex(end), "file_offset": hex(raw),
            "sha256": hashlib.sha256(data).hexdigest(),
            "entry_bytes_16": data[:16].hex(" "),
        }
        if objdump:
            result = subprocess.run(
                [objdump, "-d", "-Mintel", f"--start-address={start:#x}",
                 f"--stop-address={end:#x}", str(pe.path)],
                check=True, capture_output=True, text=True,
            )
            (output / f"{name}.asm.txt").write_text(result.stdout, encoding="utf-8")
    for name, va, count in [("main_renderer_vtable", 0xEAC84C, 14),
                            ("camera_vtable", 0xE85B50, 3),
                            ("velocity_depth_vtable", 0xECA3A8, 27),
                            ("velocity_depth_factory_vtable", 0xEC8884, 11),
                            ("camera_node_vtable", 0xF075E8, 21),
                            ("d3d11_device_vtable", 0xF06AB8, 84)]:
        raw, data = pe.read_va(va, count * 4)
        report["tables"][name] = {
            "va": hex(va), "rva": hex(va - pe.base), "file_offset": hex(raw),
            "entries": [hex(x) for x in struct.unpack(f"<{count}I", data)],
        }
    for name, va, target in [("main_to_outer", 0x58064B, 0x98A270),
                              ("auxiliary_to_outer_1", 0x57FC56, 0x98A270),
                              ("auxiliary_to_outer_2", 0x58AA61, 0x98A270),
                              ("auxiliary_to_outer_3", 0x58C6EC, 0x98A270),
                              ("auxiliary_to_outer_4", 0x5AA198, 0x98A270),
                              ("auxiliary_to_outer_5", 0x5ADB64, 0x98A270),
                              ("outer_to_inner_first", 0x98A383, 0x984A50),
                              ("outer_to_inner_second", 0x98A418, 0x984A50),
                              ("inner_depth_camera_setup", 0x984A8B, 0x97A9B0),
                              ("inner_colour_camera_setup", 0x984ADF, 0x97A9B0),
                              ("foreground_camera_setup", 0x58071D, 0x97A9B0),
                              ("visibility_camera_setup", 0x580914, 0x97A9B0),
                              ("main_frustum_build", 0x580932, 0x7C55D0),
                              ("main_frustum_copy", 0x58093A, 0x574C10)]:
        raw, data = pe.read_va(va, 5)
        if data[0] != 0xE8 or va + 5 + struct.unpack_from("<i", data, 1)[0] != target:
            raise ValueError(f"Direct-call mismatch: {name}")
        report["calls"][name] = {
            "va": hex(va), "rva": hex(va - pe.base), "file_offset": hex(raw),
            "target_va": hex(target), "return_rva": hex(va + 5 - pe.base),
            "bytes": data.hex(" "),
        }
    report["observed_layout"] = {
        "main_camera_offsets": ["0x600", "0x690"], "camera_size": "0x90",
        "list_header_size": "0x108", "context_access_minimum": "0x1d0",
        "render_serial_offset": "0xc4",
        "camera_node_size": "0x1d0",
        "camera_node_projection_offset": "0x110",
        "camera_node_view_offset": "0x150",
        "camera_node_combined_offset": "0x190",
        "main_visibility_offset": "0x350", "frustum_size": "0xe0",
        "note": "Layout applies only to the selected main renderer; complete pointee layouts are unknown",
    }
    for name, va in {
        "benchmark_switch": 0xEB434C,
        "benchmark_default_file": 0xEB4334,
        "hardware_attribute": 0xEC2AA0,
        "hardware_default_file": 0xEC2B68,
        "hardware_virtual_prefix": 0xF27AC8,
        "hardware_physical_suffix": 0xF27AAC,
        "hardware_system_prefix": 0xF27AC0,
        "game_documents_name": 0xEA6D14,
        "games_documents_name": 0xEB8244,
        "benchmark_result_path": 0xEC2A7C,
        "parallel_update_render_attribute": 0xEACD50,
        "velocity_depth_plugin_name": 0xECB440,
        "camera_node_name": 0xF07764,
    }.items():
        raw, data = pe.read_va(va, 128)
        report["strings"][name] = {
            "va": hex(va), "file_offset": hex(raw),
            "value": data.split(b"\0", 1)[0].decode("ascii"),
        }
    destination = output / "static-render-evidence.json"
    destination.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(RANGES)} ranges and {len(report['calls'])} callsites: {destination}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--objdump", help="Path to GNU objdump; optional disassembly export")
    args = parser.parse_args()
    inspect(args.executable, args.output, args.objdump)
