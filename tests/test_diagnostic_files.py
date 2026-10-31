import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from diagnostic_files import DiagnosticFiles, restore_xml_formatting, sha256


class DiagnosticFilesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.game = self.root / "game"
        self.game.mkdir()
        self.journal = self.root / "journal.json"
        self.run = "a" * 32

    def test_partial_preparation_recovers_exact_owned_files(self):
        transaction = DiagnosticFiles(self.game, self.journal, self.run)
        transaction.add("d3d11.dll", b"our-proxy")
        self.assertEqual(DiagnosticFiles.recover(self.journal, self.game), [])
        self.assertFalse((self.game / "d3d11.dll").exists())
        self.assertTrue(json.loads(self.journal.read_text())["restored"])
        self.assertEqual(DiagnosticFiles.recover(self.journal, self.game), [])

    def test_foreign_proxy_is_preserved(self):
        (self.game / "d3d11.dll").write_bytes(b"foreign")
        transaction = DiagnosticFiles(self.game, self.journal, self.run)
        with self.assertRaises(FileExistsError):
            transaction.add("d3d11.dll", b"our-proxy")
        self.assertEqual(DiagnosticFiles.recover(self.journal, self.game), [])
        self.assertEqual((self.game / "d3d11.dll").read_bytes(), b"foreign")

    def test_external_edit_retains_all_files_and_journal(self):
        transaction = DiagnosticFiles(self.game, self.journal, self.run)
        transaction.add("d3d11.dll", b"our-proxy")
        transaction.add(f"dirt3vr-benchmark-{self.run}.xml", b"config")
        (self.game / "d3d11.dll").write_bytes(b"external-edit")
        self.assertEqual(DiagnosticFiles.recover(self.journal, self.game), [str(self.game / "d3d11.dll")])
        self.assertTrue((self.game / f"dirt3vr-benchmark-{self.run}.xml").exists())
        self.assertNotIn("restored", json.loads(self.journal.read_text()))

    def test_tampered_path_refused_before_removal(self):
        transaction = DiagnosticFiles(self.game, self.journal, self.run)
        transaction.add("d3d11.dll", b"our-proxy")
        record = json.loads(self.journal.read_text())
        record["files"].append({"name": "../save.bin", "sha256": "0" * 64})
        self.journal.write_text(json.dumps(record))
        with self.assertRaises(ValueError):
            DiagnosticFiles.recover(self.journal, self.game)
        self.assertTrue((self.game / "d3d11.dll").exists())

    def test_wrong_game_refused(self):
        DiagnosticFiles(self.game, self.journal, self.run)
        with self.assertRaises(ValueError):
            DiagnosticFiles.recover(self.journal, self.root)

    def test_hardware_file_recovers_in_explicit_settings_directory(self):
        hardware = self.root / "hardwaresettings"
        hardware.mkdir()
        transaction = DiagnosticFiles(self.game, self.journal, self.run, hardware)
        name = f"dirt3vr-hardware-{self.run}.xml"
        target = transaction.add(name, b"settings")
        self.assertEqual(target, hardware / name)
        self.assertFalse((self.game / name).exists())
        self.assertEqual(DiagnosticFiles.recover(self.journal, self.game, hardware), [])
        self.assertFalse(target.exists())

    def test_tampered_hardware_directory_refused_before_proxy_removal(self):
        hardware = self.root / "hardwaresettings"
        hardware.mkdir()
        transaction = DiagnosticFiles(self.game, self.journal, self.run, hardware)
        transaction.add("d3d11.dll", b"proxy")
        record = json.loads(self.journal.read_text())
        record["hardware"] = str(self.root)
        self.journal.write_text(json.dumps(record))
        with self.assertRaises(ValueError):
            DiagnosticFiles.recover(self.journal, self.game, hardware)
        self.assertTrue((self.game / "d3d11.dll").exists())

    def test_native_xml_reformat_restores_original_bytes(self):
        original = self.root / "original.xml"
        original.write_bytes(b'<settings><display width="1280" /></settings>')
        target = self.root / "settings.xml"
        target.write_bytes(b'<settings>\n\t<display width="1280"/>\n</settings>')
        changed = target.read_bytes()
        retained = self.root / "generated.xml"
        self.assertTrue(restore_xml_formatting(target, original, sha256(original), sha256(target), retained))
        self.assertEqual(target.read_bytes(), original.read_bytes())
        self.assertEqual(retained.read_bytes(), changed)

    def test_real_xml_setting_changes_are_preserved(self):
        original = self.root / "original.xml"
        original.write_bytes(b'<settings width="1280"/>')
        target = self.root / "settings.xml"
        target.write_bytes(b'<settings width="1920"/>')
        self.assertFalse(restore_xml_formatting(target, original, sha256(original), sha256(target), self.root / "generated.xml"))
        self.assertEqual(target.read_bytes(), b'<settings width="1920"/>')


if __name__ == "__main__":
    unittest.main()
