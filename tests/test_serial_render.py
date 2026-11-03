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
from run_benchmark import diagnostic_hardware, listed_sizes


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

    def test_diagnostic_input_sets_only_the_requested_multisampling(self):
        original = (b'<hardware_settings_config version="56"><cpu><threadStrategy parallelUpdateRender="true"/></cpu>'
                    b'<graphics_card><directx forcedx9="true"/><stereo enabled="true"/><resolution width="1920" height="1080" multisampling="8xcsaa"/></graphics_card>'
                    b'</hardware_settings_config>')
        self.assertEqual(ET.fromstring(diagnostic_hardware(original)).find("graphics_card/resolution").get("multisampling"), "off")
        for level in ("2xmsaa", "4xmsaa", "8xmsaa"):
            with self.subTest(level=level):
                resolution = ET.fromstring(diagnostic_hardware(original, multisampling=level)).find("graphics_card/resolution")
                self.assertEqual((resolution.get("multisampling"), resolution.get("width"), resolution.get("fullscreen")), (level, "1280", "false"))
        for unsupported in ("16xmsaa", "8xcsaa", "4", ""):
            with self.subTest(unsupported=unsupported), self.assertRaises(ValueError):
                diagnostic_hardware(original, multisampling=unsupported)

    def test_diagnostic_input_sets_the_requested_size(self):
        original = (b'<hardware_settings_config version="56"><cpu><threadStrategy parallelUpdateRender="true"/></cpu>'
                    b'<graphics_card><directx forcedx9="true"/><stereo enabled="true"/><resolution width="1920" height="1080" fullscreen="true"/></graphics_card>'
                    b'</hardware_settings_config>')
        resolution = ET.fromstring(diagnostic_hardware(original, size=(3072, 3264), multisampling="4xmsaa")).find("graphics_card/resolution")
        self.assertEqual((resolution.get("width"), resolution.get("height"), resolution.get("fullscreen"), resolution.get("multisampling")),
                         ("3072", "3264", "false", "4xmsaa"))
        for unsupported in ((639, 720), (1280, 4097), (1280,), (1280.0, 720)):
            with self.subTest(unsupported=unsupported), self.assertRaises(ValueError):
                diagnostic_hardware(original, size=unsupported)

    def test_only_sizes_the_game_lists_are_runnable(self):
        info = (b'<hardware_settings_info><graphics_card><resolutions><resolution width="1280" height="720" aspect="auto">'
                b'<refreshRate rate="60"/></resolution><resolution width="3840" height="2160" aspect="auto"/>'
                b'<resolution width="bad" height="1"/></resolutions></graphics_card></hardware_settings_info>')
        self.assertEqual(listed_sizes(info), {(1280, 720), (3840, 2160)})
        self.assertNotIn((3072, 3264), listed_sizes(info))


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

    def test_external_restriction_edit_keeps_that_file_and_backup_and_removes_the_proxy(self):
        self.transaction().apply_serial_render(self.original)
        self.target.write_bytes(b"external edit")
        self.assertEqual(self.recover(), [str(self.target)])
        self.assertEqual(self.target.read_bytes(), b"external edit")
        self.assertFalse((self.game / "d3d11.dll").exists())
        self.assertFalse(json.loads(self.journal.read_text()).get("restored", False))
        self.assertEqual((self.root / f"restrictions-original-{self.run_id}.bin").read_bytes(), self.original)

    def test_proxy_conflict_keeps_the_proxy_and_still_restores_the_original(self):
        self.transaction().apply_serial_render(self.original)
        (self.game / "d3d11.dll").write_bytes(b"external proxy")
        self.assertEqual(self.recover(), [str(self.game / "d3d11.dll")])
        self.assertEqual((self.game / "d3d11.dll").read_bytes(), b"external proxy")
        self.assertEqual(self.target.read_bytes(), self.original)

    def test_missing_original_is_a_conflict_not_permission_to_recreate_it(self):
        self.transaction().apply_serial_render(self.original)
        self.target.unlink()
        self.assertEqual(self.recover(), [str(self.target)])
        self.assertFalse(self.target.exists())
        self.assertFalse((self.game / "d3d11.dll").exists())

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


SPECTATOR_FIXTURE = (b'<?xml version="1.0" encoding="UTF-8" ?>\r\n<Cutscene>\r\n\t<Timeline>\r\n'
                     b'\t\t<Keyframe name="kf_start" time="0.000000" timeType="absolute">\r\n'
                     b'\t\t\t<Event type="StartLights" object="core_game_startlight_">\r\n\t\t\t\t<Reset>\r\n'
                     b'\t\t\t\t\t<State texture="start_lights_6_green_e.tga" type="green" />\r\n\t\t\t\t</Reset>\r\n'
                     b'\t\t\t</Event>\r\n\t\t</Keyframe>\r\n\t</Timeline>\r\n</Cutscene>\r\n')


class SpectatorSwitchTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game = self.root / "game"
        (self.game / "system").mkdir(parents=True)
        (self.game / "cutscene").mkdir()
        self.restrictions = restrictions_fixture()
        (self.game / df.RESTRICTIONS_NAME).write_bytes(self.restrictions)
        self.target = self.game / df.SPECTATOR_NAME
        self.target.write_bytes(SPECTATOR_FIXTURE)
        for name, data in (("RESTRICTIONS_SHA256", self.restrictions), ("SPECTATOR_SHA256", SPECTATOR_FIXTURE)):
            pin = patch.object(df, name, hashlib.sha256(data).hexdigest())
            pin.start()
            self.addCleanup(pin.stop)
        self.run_id = "c" * 32
        self.journal = self.root / "deployment.json"

    def recover(self):
        return df.DiagnosticFiles.recover(self.journal, self.game)

    def test_switch_hands_the_car_to_the_player_and_keeps_every_other_byte(self):
        applied = df.spectator_human_switch(SPECTATOR_FIXTURE)
        self.assertEqual(applied.replace(df.HUMAN_SWITCH, b""), SPECTATOR_FIXTURE)
        events = ET.fromstring(applied).findall("Timeline/Keyframe/Event")
        self.assertEqual([e.get("type") for e in events], ["StartLights", "Controller"])
        self.assertEqual(events[1].find("Switch").get("type"), "human")
        with self.assertRaises(ValueError):
            df.spectator_human_switch(SPECTATOR_FIXTURE + b" ")

    def test_both_replacements_restore_byte_exact(self):
        transaction = df.DiagnosticFiles(self.game, self.journal, self.run_id)
        transaction.add("d3d11.dll", b"owned proxy")
        transaction.apply_serial_render(self.restrictions)
        transaction.apply_replacement(df.SPECTATOR_NAME, SPECTATOR_FIXTURE)
        self.assertEqual(self.target.read_bytes(), df.spectator_human_switch(SPECTATOR_FIXTURE))
        with self.assertRaises(ValueError):
            transaction.apply_replacement(df.SPECTATOR_NAME, SPECTATOR_FIXTURE)
        self.assertEqual(self.recover(), [])
        self.assertEqual(self.target.read_bytes(), SPECTATOR_FIXTURE)
        self.assertEqual((self.game / df.RESTRICTIONS_NAME).read_bytes(), self.restrictions)
        self.assertFalse((self.game / "d3d11.dll").exists())

    def test_changed_start_sequence_is_kept_and_the_proxy_removed(self):
        transaction = df.DiagnosticFiles(self.game, self.journal, self.run_id)
        transaction.add("d3d11.dll", b"owned proxy")
        transaction.apply_replacement(df.SPECTATOR_NAME, SPECTATOR_FIXTURE)
        self.target.write_bytes(b"game update")
        self.assertEqual(self.recover(), [str(self.target)])
        self.assertEqual(self.target.read_bytes(), b"game update")
        self.assertFalse((self.game / "d3d11.dll").exists())
        self.assertEqual((self.root / f"spectator-original-{self.run_id}.bin").read_bytes(), SPECTATOR_FIXTURE)

    def test_unknown_or_duplicate_replacement_entries_are_refused(self):
        transaction = df.DiagnosticFiles(self.game, self.journal, self.run_id)
        transaction.apply_replacement(df.SPECTATOR_NAME, SPECTATOR_FIXTURE)
        record = json.loads(self.journal.read_text())
        for replacements in ([dict(record["replacements"][0], name="cutscene/other.xml")],
                             record["replacements"] * 2, []):
            self.journal.write_text(json.dumps(dict(record, replacements=replacements)))
            with self.subTest(replacements=len(replacements)), self.assertRaises(ValueError):
                self.recover()
            self.assertEqual(self.target.read_bytes(), df.spectator_human_switch(SPECTATOR_FIXTURE))


def binary_effects(mix: str) -> bytes:
    import seat_cameras
    from seat_cameras import Node
    group = Node("ParameterGroup", None, [("name", "BloomFromDsX4")],
                 [Node("Param", f"\r\n\t\t\t{mix}\r\n\t\t", [("name", "mixHuge")], [])])
    root = Node("PostProcessEffects", None, [], [Node("Effect", None, [("name", "t~a~b")], [group])])
    return seat_cameras.encode(seat_cameras.from_tree(root, [""]))


GENERIC_FIXTURE = (b'<PostProcessEffects>\r\n\t<Effect name="Effect_0001">\r\n\t\t<ParameterGroup name="BloomFromDsX4">\r\n'
                   b'\t\t\t<Param name="mixMedium">\r\n\t\t\t\t0.069400\r\n\t\t\t</Param>\r\n'
                   b'\t\t</ParameterGroup>\r\n\t</Effect>\r\n</PostProcessEffects>\r\n')


class BloomTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game = self.root / "game"
        (self.game / "postprocess").mkdir(parents=True)
        (self.game / "tracks/generic").mkdir(parents=True)
        self.originals = {df.EFFECTS_NAME: binary_effects("1.999800"), df.GENERIC_EFFECTS_NAME: GENERIC_FIXTURE,
                          df.NIGHT_EFFECTS_NAME: binary_effects("0.606900")}
        for (name, data), pin_name in zip(self.originals.items(), ("EFFECTS_SHA256", "GENERIC_EFFECTS_SHA256", "NIGHT_EFFECTS_SHA256")):
            (self.game / name).write_bytes(data)
            pin = patch.object(df, pin_name, hashlib.sha256(data).hexdigest())
            pin.start()
            self.addCleanup(pin.stop)
        self.run_id = "d" * 32
        self.journal = self.root / "deployment.json"

    def test_bloom_files_are_zeroed_and_restored_byte_exact(self):
        transaction = df.DiagnosticFiles(self.game, self.journal, self.run_id)
        transaction.add("d3d11.dll", b"owned proxy")
        for name in df.BLOOM_NAMES:
            transaction.apply_replacement(name, self.originals[name])
        self.assertEqual((self.game / df.GENERIC_EFFECTS_NAME).read_bytes(), GENERIC_FIXTURE.replace(b"0.069400", b"0.000000"))
        for name in df.BLOOM_NAMES:
            self.assertEqual((self.game / name).read_bytes(), df.bloom.disable_bloom(self.originals[name]))
        self.assertEqual(df.DiagnosticFiles.recover(self.journal, self.game), [])
        for name, data in self.originals.items():
            self.assertEqual((self.game / name).read_bytes(), data)
        self.assertFalse((self.game / "d3d11.dll").exists())

    def test_glare_and_both_edits_of_the_effects_file_restore_byte_exact(self):
        import glare
        import seat_cameras
        from seat_cameras import Node
        flare = Node("ParameterGroup", None, [("name", "LensFlares")], [Node("Param", "\r\n\t\t\t2.000000\r\n\t\t", [("name", "multiplier")], [])])
        bloom_group = Node("ParameterGroup", None, [("name", "BloomFromDsX4")], [Node("Param", "\r\n\t\t\t1.999800\r\n\t\t", [("name", "mixHuge")], [])])
        effects = seat_cameras.encode(seat_cameras.from_tree(
            Node("PostProcessEffects", None, [], [Node("Effect", None, [("name", "t~a~b")], [flare, bloom_group])]), [""]))
        (self.game / df.EFFECTS_NAME).write_bytes(effects)
        for edits, expected in ((("glare",), glare.disable_glare(effects)),
                                (("bloom", "glare"), glare.disable_glare(df.bloom.disable_bloom(effects)))):
            with self.subTest(edits=edits), patch.object(df, "EFFECTS_SHA256", hashlib.sha256(effects).hexdigest()), \
                    patch.object(df, "EFFECTS_EDITS", edits):
                journal = self.root / f"deployment-{len(edits)}.json"
                transaction = df.DiagnosticFiles(self.game, journal, self.run_id)
                transaction.apply_replacement(df.EFFECTS_NAME, effects)
                self.assertEqual((self.game / df.EFFECTS_NAME).read_bytes(), expected)
                self.assertEqual(df.DiagnosticFiles.recover(journal, self.game), [])
                self.assertEqual((self.game / df.EFFECTS_NAME).read_bytes(), effects)
                (self.root / f"effects-original-{self.run_id}.bin").unlink()

    def test_unexpected_effects_file_is_refused_before_any_change(self):
        (self.game / df.EFFECTS_NAME).write_bytes(binary_effects("0.500000"))
        with self.assertRaises(ValueError):
            df.preflight_replacement(self.game, df.EFFECTS_NAME)


if __name__ == "__main__":
    unittest.main()
