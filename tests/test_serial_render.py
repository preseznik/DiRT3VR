import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import diagnostic_files as df
from run_benchmark import diagnostic_hardware


def restrictions_fixture():
    branches = [("cores", "int_eq", "2"), ("cores", "int_eq", "3"),
                ("cores", "int_eq", "4"), ("cores", "int_eq", "6"),
                ("cores", "int_ge", "8"), ("hyperThreading", "bo_eq", "true")]
    rules = "\r\n".join(
        f'  <restrict_if name="{name}" op="{op}" value="{value}"><restrict_add>'
        f'<threadStrategy parallelUpdateRender="true" workerMapFile="keep-{index}.xml" renderProcessor="{index}"/>'
        '</restrict_add></restrict_if>' for index, (name, op, value) in enumerate(branches))
    return ('<?xml version="1.0" encoding="UTF-8" ?>\r\n'
            '<hardware_settings_restrictions>\r\n<!-- Keep this comment and CRLF -->\r\n'
            '<cpu><restrict_info>\r\n' + rules + '\r\n'
            '<restrict_else><restrict_add><threadStrategy parallelUpdateRender="false" workerMapFile="fallback.xml"/>'
            '</restrict_add></restrict_else></restrict_info></cpu>\r\n'
            '<graphics_card parallelUpdateRender="true"><resolution width="1920"/></graphics_card>\r\n'
            '</hardware_settings_restrictions>\r\n').encode()


class SerialRenderTransformTests(unittest.TestCase):
    def test_only_six_cpu_values_change_with_all_other_bytes_preserved(self):
        original = restrictions_fixture()
        result = df.serial_render_restrictions(original)
        self.assertEqual(result, original.replace(b'parallelUpdateRender="true"', b'parallelUpdateRender="false"', 6))
        self.assertIn(b'<graphics_card parallelUpdateRender="true">', result)
        expected = ET.fromstring(original)
        for strategy in expected.findall("cpu/restrict_info/restrict_if/restrict_add/threadStrategy"):
            strategy.set("parallelUpdateRender", "false")
        self.assertEqual(df.xml_semantics(result), df.xml_semantics(ET.tostring(expected)))

    def test_unexpected_cpu_signatures_are_rejected(self):
        original = restrictions_fixture()
        for changed in (original.replace(b'value="8"', b'value="16"'),
                        original.replace(b'parallelUpdateRender="false"', b'parallelUpdateRender="true"'),
                        original.replace(b'parallelUpdateRender="true"', b'parallelUpdateRender="false"', 1),
                        original.replace(b'</cpu>', b'</cpu><cpu/>')):
            with self.subTest(changed=changed[:60]), self.assertRaises(ValueError):
                df.serial_render_restrictions(changed)

    def test_diagnostic_input_changes_scheduling_only_when_opted_in(self):
        original = (b'<hardware_settings_config version="56"><cpu><threadStrategy parallelUpdateRender="true" workerMapFile="keep.xml"/></cpu>'
                    b'<graphics_card><directx forcedx9="true"/><stereo enabled="true"/><resolution width="1920" height="1080" extra="keep"/></graphics_card>'
                    b'<audio mixing="original"/></hardware_settings_config>')
        normal = ET.fromstring(diagnostic_hardware(original))
        serial = ET.fromstring(diagnostic_hardware(original, serial_render=True))
        self.assertEqual(normal.find("cpu/threadStrategy").get("parallelUpdateRender"), "true")
        self.assertEqual(serial.find("cpu/threadStrategy").get("parallelUpdateRender"), "false")
        self.assertEqual(serial.find("cpu/threadStrategy").get("workerMapFile"), "keep.xml")
        self.assertEqual(serial.find("audio").get("mixing"), "original")
        self.assertEqual(serial.find("graphics_card/resolution").get("extra"), "keep")
        self.assertIn(b'parallelUpdateRender="true"', original)


class SerialRenderTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game = self.root / "game"
        (self.game / "system").mkdir(parents=True)
        self.hardware = self.root / "actual-documents" / "My Games" / "DiRT3" / "hardwaresettings"
        self.hardware.mkdir(parents=True)
        self.user_settings = self.hardware / "hardware_settings_config.xml"
        self.user_settings.write_bytes(b"existing user settings")
        self.original = restrictions_fixture()
        self.target = self.game / df.RESTRICTIONS_NAME
        self.target.write_bytes(self.original)
        # The production hash remains pinned; fixtures independently pin their own bytes.
        pin = patch.object(df, "RESTRICTIONS_SHA256", hashlib.sha256(self.original).hexdigest())
        pin.start()
        self.addCleanup(pin.stop)
        self.run_id = "b" * 32
        self.journal = self.root / "deployment.json"

    def transaction(self):
        result = df.DiagnosticFiles(self.game, self.journal, self.run_id, self.hardware)
        result.add("d3d11.dll", b"owned proxy")
        result.add(f"dirt3vr-hardware-{self.run_id}.xml", b"unique diagnostic input")
        return result

    def recover(self):
        return df.DiagnosticFiles.recover(self.journal, self.game, self.hardware)

    def test_preflight_is_read_only_and_rejects_changed_original(self):
        original, applied = df.preflight_serial_render(self.game)
        self.assertEqual(original, self.original)
        self.assertNotEqual(original, applied)
        self.assertEqual(self.target.read_bytes(), original)
        self.assertFalse(self.journal.exists())
        self.target.write_bytes(original + b" ")
        with self.assertRaises(ValueError):
            df.preflight_serial_render(self.game)
        self.assertFalse(self.journal.exists())

    def test_original_backup_and_journal_exist_before_first_replacement(self):
        transaction = self.transaction()
        with patch.object(df, "replace_verified", side_effect=RuntimeError("interrupted before write")):
            with self.assertRaises(RuntimeError):
                transaction.apply_serial_render(self.original)
        record = json.loads(self.journal.read_text())
        self.assertEqual(record["schema"], 3)
        self.assertEqual((self.root / record["replacements"][0]["backup"]).read_bytes(), self.original)
        self.assertEqual(self.target.read_bytes(), self.original)
        self.assertEqual(self.recover(), [])
        self.assertFalse((self.game / "d3d11.dll").exists())

    def test_interrupted_applied_session_restores_exact_original(self):
        transaction = self.transaction()
        transaction.apply_serial_render(self.original)
        self.assertEqual(self.target.read_bytes(), df.serial_render_restrictions(self.original))
        self.assertEqual(self.recover(), [])
        self.assertEqual(self.target.read_bytes(), self.original)
        self.assertEqual(self.user_settings.read_bytes(), b"existing user settings")
        self.assertFalse((self.hardware / f"dirt3vr-hardware-{self.run_id}.xml").exists())
        self.assertEqual(self.recover(), [])

    def test_external_restriction_edit_preserves_every_file_and_backup(self):
        self.transaction().apply_serial_render(self.original)
        self.target.write_bytes(b"external edit")
        self.assertEqual(self.recover(), [str(self.target)])
        self.assertEqual(self.target.read_bytes(), b"external edit")
        self.assertTrue((self.game / "d3d11.dll").exists())
        self.assertFalse(json.loads(self.journal.read_text()).get("restored", False))
        self.assertEqual((self.root / f"restrictions-original-{self.run_id}.bin").read_bytes(), self.original)

    def test_proxy_conflict_stops_original_restoration_during_full_preflight(self):
        self.transaction().apply_serial_render(self.original)
        (self.game / "d3d11.dll").write_bytes(b"external proxy")
        self.assertEqual(self.recover(), [str(self.game / "d3d11.dll")])
        self.assertEqual(self.target.read_bytes(), df.serial_render_restrictions(self.original))

    def test_missing_original_is_a_conflict_not_permission_to_recreate_it(self):
        self.transaction().apply_serial_render(self.original)
        self.target.unlink()
        self.assertEqual(self.recover(), [str(self.target)])
        self.assertFalse(self.target.exists())
        self.assertTrue((self.game / "d3d11.dll").exists())

    def test_bad_backup_blocks_all_recovery(self):
        self.transaction().apply_serial_render(self.original)
        backup = self.root / f"restrictions-original-{self.run_id}.bin"
        backup.write_bytes(b"damaged")
        with self.assertRaises(ValueError):
            self.recover()
        self.assertTrue((self.game / "d3d11.dll").exists())
        self.assertEqual(self.target.read_bytes(), df.serial_render_restrictions(self.original))

    def test_tampered_replacement_fields_reject_before_any_mutation(self):
        self.transaction().apply_serial_render(self.original)
        original_record = json.loads(self.journal.read_text())
        for key, value in (("name", "../save.bin"), ("backup", "../outside.bin"),
                           ("originalSha256", "0" * 64), ("appliedSha256", "0" * 64),
                           ("appliedSha256", None)):
            record = json.loads(json.dumps(original_record))
            record["replacements"][0][key] = value
            self.journal.write_text(json.dumps(record))
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.recover()
            self.assertTrue((self.game / "d3d11.dll").exists())
            self.assertEqual(self.target.read_bytes(), df.serial_render_restrictions(self.original))
        original_record["files"].append({"name": "../outside", "sha256": "0" * 64})
        self.journal.write_text(json.dumps(original_record))
        with self.assertRaises(ValueError):
            self.recover()
        self.assertEqual(self.target.read_bytes(), df.serial_render_restrictions(self.original))

    def test_applied_original_is_rechecked_before_atomic_replacement(self):
        actual_sha256 = df.sha256
        reads = 0

        def changed_after_observation(path):
            nonlocal reads
            digest = actual_sha256(path)
            if path == self.target:
                reads += 1
                if reads == 1:
                    path.write_bytes(b"external concurrent edit")
            return digest

        with patch.object(df, "sha256", side_effect=changed_after_observation):
            with self.assertRaises(ValueError):
                df.replace_verified(self.target, b"replacement", actual_sha256(self.target))
        self.assertEqual(self.target.read_bytes(), b"external concurrent edit")
        self.assertEqual(list(self.target.parent.glob("*.tmp")), [])


if __name__ == "__main__":
    unittest.main()
