"""Owned file transaction for the bounded DiRT 3 diagnostic runner.

Temporary files use exclusive creation. The only replaceable original is the
fingerprinted CPU restrictions file, with an exact backup and journal before
its six parallel-render overrides are disabled. Conflicts retain all evidence.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

RESTRICTIONS_NAME = "system/hardware_settings_restrictions.xml"
RESTRICTIONS_SHA256 = "45257db5fa64d833575d569ec6f3b03344b5f40be25830960a8d11783276470b"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def no_links(path: Path) -> None:
    for item in (path, *path.parents):
        if item.exists() or item.is_symlink():
            info = item.lstat()
            if item.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ValueError(f"Reparse/symlink path refused: {item}")


def save_json(path: Path, value: dict) -> None:
    no_links(path)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def xml_semantics(data: bytes):
    """Ignore indentation and attribute order, preserving every setting/value."""
    def node(element):
        return (element.tag, tuple(sorted(element.attrib.items())),
                (element.text or "").strip(), (element.tail or "").strip(),
                tuple(node(child) for child in element))
    return node(ET.fromstring(data))


def serial_render_restrictions(data: bytes) -> bytes:
    """Change only the six known CPU overrides; retain all other original bytes."""
    root = ET.fromstring(data)
    cpu = root.findall("cpu")
    if root.tag != "hardware_settings_restrictions" or len(cpu) != 1:
        raise ValueError("Unexpected hardware restrictions root/CPU signature")
    branches = cpu[0].findall("restrict_info/restrict_if")
    expected = [("cores", "int_eq", "2"), ("cores", "int_eq", "3"),
                ("cores", "int_eq", "4"), ("cores", "int_eq", "6"),
                ("cores", "int_ge", "8"), ("hyperThreading", "bo_eq", "true")]
    if [(b.get("name"), b.get("op"), b.get("value")) for b in branches] != expected:
        raise ValueError("Unexpected CPU restriction branch signature")
    strategies = []
    for branch in branches:
        found = branch.findall("restrict_add/threadStrategy")
        if len(found) != 1 or found[0].get("parallelUpdateRender") != "true":
            raise ValueError("Unexpected CPU parallel-render override signature")
        strategies.append(found[0])
    fallback = cpu[0].findall("restrict_info/restrict_else/restrict_add/threadStrategy")
    if (len(fallback) != 1 or fallback[0].get("parallelUpdateRender") != "false"
            or len(root.findall(".//threadStrategy")) != 7):
        raise ValueError("Unexpected CPU restriction fallback signature")
    matches = list(re.finditer(rb"<cpu\b[^>]*>.*?</cpu\s*>", data, re.DOTALL))
    if len(matches) != 1 or matches[0].group().count(b'parallelUpdateRender="true"') != 6:
        raise ValueError("Unexpected CPU restriction byte signature")
    block = matches[0]
    applied = (data[:block.start()] + block.group().replace(b'parallelUpdateRender="true"',
                                                          b'parallelUpdateRender="false"')
               + data[block.end():])
    for strategy in strategies:
        strategy.set("parallelUpdateRender", "false")
    if xml_semantics(applied) != xml_semantics(ET.tostring(root)):
        raise ValueError("Serial-render transformation changed unrelated XML settings")
    return applied


def preflight_serial_render(game: Path) -> tuple[bytes, bytes]:
    target = game / RESTRICTIONS_NAME
    no_links(target)
    original = target.read_bytes()
    if hashlib.sha256(original).hexdigest() != RESTRICTIONS_SHA256:
        raise ValueError("Unsupported hardware restrictions file; no serial-render patch applied")
    if getattr(target.stat(), "st_file_attributes", 0) & 1:
        raise PermissionError("The hardware restrictions file is read-only; it was preserved")
    return original, serial_render_restrictions(original)


def replace_verified(target: Path, data: bytes, expected_hash: str) -> None:
    """Replace only the observed owned state, using a flushed same-directory file."""
    no_links(target)
    if not target.is_file() or sha256(target) != expected_hash:
        raise ValueError(f"File changed before replacement; current file preserved: {target}")
    temporary = target.with_name(target.name + ".dirt3vr-" + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        no_links(target)
        if not target.is_file() or sha256(target) != expected_hash:
            raise ValueError(f"File changed during replacement; current file preserved: {target}")
        os.replace(temporary, target)
        if sha256(target) != hashlib.sha256(data).hexdigest():
            raise IOError(f"Replacement verification failed: {target}")
    finally:
        if temporary.exists():
            temporary.unlink()


def restore_xml_formatting(target: Path, backup: Path, original_hash: str,
                           observed_hash: str, retained: Path) -> bool:
    """Restore only proven formatting-only changes, preserving the generated copy."""
    for path in (target, backup, retained):
        no_links(path)
    original, current = backup.read_bytes(), target.read_bytes()
    if hashlib.sha256(original).hexdigest() != original_hash or hashlib.sha256(current).hexdigest() != observed_hash:
        raise ValueError("XML changed after observation; original and current files preserved")
    try:
        if xml_semantics(original) != xml_semantics(current):
            return False
    except ET.ParseError:
        return False
    with retained.open("xb") as stream:
        stream.write(current)
    temporary = target.with_name(target.name + ".dirt3vr-restore-" + uuid.uuid4().hex)
    with temporary.open("xb") as stream:
        stream.write(original)
        stream.flush()
        os.fsync(stream.fileno())
    try:
        if sha256(target) != observed_hash:
            raise ValueError("XML changed during restoration; current file preserved")
        os.replace(temporary, target)
    finally:
        if temporary.exists():
            temporary.unlink()
    if sha256(target) != original_hash:
        raise IOError("XML restoration verification failed")
    return True


class DiagnosticFiles:
    def __init__(self, game: Path, journal: Path, run_id: str, hardware: Path | None = None):
        if not re.fullmatch(r"[a-f0-9]{32}", run_id):
            raise ValueError("Invalid diagnostic run ID")
        self.game = game.absolute()
        self.journal = journal.absolute()
        self.run_id = run_id
        self.hardware = hardware.absolute() if hardware else None
        no_links(self.game)
        if self.hardware:
            no_links(self.hardware)
            if not self.hardware.is_dir():
                raise ValueError("The game's hardware-settings directory must already exist")
        no_links(self.journal)
        if self.journal.exists():
            raise FileExistsError("Recover the previous journal first")
        self.record = {"schema": 1, "game": str(self.game), "runId": run_id, "files": []}
        if self.hardware:
            self.record.update(schema=2, hardware=str(self.hardware))
        save_json(self.journal, self.record)

    def apply_serial_render(self, original: bytes) -> Path:
        """Journal one supported replacement before changing the installed file."""
        if self.record.get("replacements"):
            raise ValueError("Serial-render restrictions are already recorded")
        observed, applied = preflight_serial_render(self.game)
        if observed != original:
            raise ValueError("Hardware restrictions changed since preflight")
        target = self.game / RESTRICTIONS_NAME
        backup_name = f"restrictions-original-{self.run_id}.bin"
        backup = self.journal.parent / backup_name
        no_links(backup)
        with backup.open("xb") as stream:
            stream.write(original)
            stream.flush()
            os.fsync(stream.fileno())
        original_hash = hashlib.sha256(original).hexdigest()
        if sha256(backup) != original_hash:
            raise IOError("Hardware restrictions backup verification failed")
        self.record.update(schema=3, replacements=[{
            "name": RESTRICTIONS_NAME, "backup": backup_name,
            "originalSha256": original_hash, "appliedSha256": hashlib.sha256(applied).hexdigest()}])
        save_json(self.journal, self.record)
        replace_verified(target, applied, original_hash)
        return target

    def add(self, name: str, data: bytes) -> Path:
        if name not in self.names(self.run_id):
            raise ValueError("Unsupported diagnostic filename")
        target = self.target(self.game, self.hardware, name)
        no_links(target)
        if target.exists():
            raise FileExistsError(f"Existing file preserved: {target}")
        entry = {"name": name, "sha256": hashlib.sha256(data).hexdigest()}
        if any(item["name"] == name for item in self.record["files"]):
            raise ValueError("Duplicate diagnostic file")
        self.record["files"].append(entry)
        save_json(self.journal, self.record)
        # Exclusive creation avoids replacing a foreign proxy created after preflight.
        with target.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if sha256(target) != entry["sha256"]:
            raise IOError(f"Diagnostic write verification failed: {target}")
        return target

    @staticmethod
    def names(run_id: str) -> set[str]:
        return {"d3d11.dll", f"dirt3vr-benchmark-{run_id}.xml", f"dirt3vr-hardware-{run_id}.xml"}

    @staticmethod
    def target(game: Path, hardware: Path | None, name: str) -> Path:
        return (hardware if hardware and name.startswith("dirt3vr-hardware-") else game) / name

    @staticmethod
    def recover(journal: Path, expected_game: Path, expected_hardware: Path | None = None) -> list[str]:
        no_links(journal)
        record = json.loads(journal.read_text(encoding="utf-8"))
        game = expected_game.absolute()
        no_links(game)
        if not isinstance(record, dict):
            raise ValueError("Invalid diagnostic journal")
        run_id = record.get("runId", "")
        if (type(record.get("schema")) is not int or record.get("schema") not in (1, 2, 3)
                or record.get("game") != str(game) or not isinstance(run_id, str)
                or not re.fullmatch(r"[a-f0-9]{32}", run_id)):
            raise ValueError("Diagnostic journal identity mismatch")
        hardware = None
        if record["schema"] == 2 or (record["schema"] == 3 and "hardware" in record):
            hardware = expected_hardware.absolute() if expected_hardware else None
            if hardware is None or record.get("hardware") != str(hardware):
                raise ValueError("Diagnostic hardware-settings directory mismatch")
            no_links(hardware)
        entries = record.get("files")
        if not isinstance(entries, list) or len(entries) > 3:
            raise ValueError("Invalid diagnostic file list")
        names = set()
        conflicts = []
        replacements = record.get("replacements", [])
        if (not isinstance(replacements, list) or len(replacements) > 1
                or (record["schema"] < 3 and "replacements" in record)
                or (record["schema"] == 3 and len(replacements) != 1)):
            raise ValueError("Invalid diagnostic replacement list")
        originals = []
        for entry in replacements:
            expected_backup = f"restrictions-original-{run_id}.bin"
            if (not isinstance(entry, dict) or entry.get("name") != RESTRICTIONS_NAME
                    or entry.get("backup") != expected_backup
                    or entry.get("originalSha256") != RESTRICTIONS_SHA256
                    or not isinstance(entry.get("appliedSha256"), str)
                    or not re.fullmatch(r"[a-f0-9]{64}", entry["appliedSha256"])):
                raise ValueError("Invalid hardware restrictions recovery entry")
            target = game / RESTRICTIONS_NAME
            backup = journal.parent / expected_backup
            no_links(target)
            no_links(backup)
            if not backup.is_file() or sha256(backup) != entry["originalSha256"]:
                raise ValueError("Original hardware restrictions backup is missing or changed")
            original = backup.read_bytes()
            if hashlib.sha256(original).hexdigest() != entry["originalSha256"]:
                raise ValueError("Original hardware restrictions backup changed during preflight")
            applied = serial_render_restrictions(original)
            if hashlib.sha256(applied).hexdigest() != entry["appliedSha256"]:
                raise ValueError("Hardware restrictions applied-state signature mismatch")
            if not target.is_file() or sha256(target) not in (entry["originalSha256"], entry["appliedSha256"]):
                conflicts.append(str(target))
            originals.append((target, original, entry))
        # Validate the complete journal before deleting anything.
        for entry in entries:
            if not isinstance(entry, dict):
                raise ValueError("Invalid diagnostic file entry")
            name = entry.get("name", "")
            if (not isinstance(name, str) or name not in DiagnosticFiles.names(run_id) or name in names
                    or not isinstance(entry.get("sha256"), str)
                    or not re.fullmatch(r"[a-f0-9]{64}", entry["sha256"])):
                raise ValueError("Invalid diagnostic file entry")
            names.add(name)
            target = DiagnosticFiles.target(game, hardware, name)
            no_links(target)
            if target.exists() and sha256(target) != entry["sha256"]:
                conflicts.append(str(target))
        if conflicts:
            return conflicts
        for target, original, entry in originals:
            no_links(target)
            if not target.is_file():
                return [str(target)]
            current = sha256(target)
            if current == entry["originalSha256"]:
                continue  # Interrupted before application, or already restored.
            if current != entry["appliedSha256"]:
                return [str(target)]
            replace_verified(target, original, entry["appliedSha256"])
        for entry in entries:
            target = DiagnosticFiles.target(game, hardware, entry["name"])
            if target.exists():
                # Recheck immediately before removal; never recursively delete.
                no_links(target)
                if sha256(target) != entry["sha256"]:
                    return [str(target)]
                target.unlink()
        record["restored"] = True
        save_json(journal, record)
        return []
