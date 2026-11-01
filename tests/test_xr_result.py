import contextlib
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import run_benchmark as runner


def frame(main=1000, total=1, **overrides):
    values = {"main": main, "submitted": 1, "total": total, "cameras_restored": 1,
              "history_before": 8, "history_first": 9, "history_second": 9,
              "history_expected_advance": 1, "projections": "2/2"}
    values.update(overrides)
    return "[12:34:56] XR prototype " + " ".join(f"{key}={value}" for key, value in values.items())


SHUTDOWN = "XR prototype stopped reason=runner-stop-file\nXR prototype shutdown complete"


class XrEvidenceTests(unittest.TestCase):
    def test_sparse_submissions_and_shutdown_are_required_without_claiming_stereo(self):
        result = runner.summarize_xr("\n".join([frame(), frame(1300, 240), SHUTDOWN]))
        self.assertTrue(result["evidenceVerified"])
        self.assertEqual(result["submittedFrames"], 240)
        self.assertFalse(result["stereoVerified"])

    def test_no_frames_insufficient_frames_or_no_shutdown_are_not_acceptance(self):
        for lines in ([SHUTDOWN], [frame(total=119), SHUTDOWN], [frame(total=120)],
                      [frame(total=120), "XR prototype shutdown complete"]):
            with self.subTest(lines=lines):
                self.assertFalse(runner.summarize_xr("\n".join(lines))["evidenceVerified"])

    def test_corrupt_or_failed_frame_is_rejected(self):
        for key, value in (("main", 0), ("submitted", 0), ("cameras_restored", 0),
                           ("history_first", 0), ("history_second", 0), ("projections", "0/2"),
                           ("projections", "2"), ("history_before", -1), ("history_expected_advance", 2)):
            with self.subTest(key=key, value=value):
                self.assertFalse(runner.summarize_xr(frame(total=120, **{key: value}) + "\n" + SHUTDOWN)["evidenceVerified"])

    def test_wrap_and_explicit_no_advance_are_valid(self):
        for kwargs in ({"history_before": 0xffffffff, "history_first": 0, "history_second": 0,
                        "history_expected_advance": 1},
                       {"history_first": 8, "history_second": 8, "history_expected_advance": 0}):
            with self.subTest(kwargs=kwargs):
                self.assertTrue(runner.summarize_xr(frame(total=120, **kwargs) + "\n" + SHUTDOWN)["evidenceVerified"])
        self.assertFalse(runner.summarize_xr(frame(total=120, history_expected_advance=0) + "\n" + SHUTDOWN)["evidenceVerified"])

    def test_non_monotonic_totals_sequences_and_duplicate_fields_fail(self):
        for second in (frame(999, 120), frame(1000, 120), frame(1300, 1),
                       frame(1300, 120) + " total=120", "XR prototype main=1001"):
            with self.subTest(second=second):
                self.assertFalse(runner.summarize_xr("\n".join([frame(), second, SHUTDOWN]))["evidenceVerified"])

    def test_future_named_fields_allowed_but_failure_and_unexpected_stop_rejected(self):
        base = frame(total=120) + " future=observed\n" + SHUTDOWN
        self.assertTrue(runner.summarize_xr(base)["evidenceVerified"])
        for failure in ("XR prototype failed main=1", "XR prototype refused reason=guard",
                        "XR prototype stopped reason=initialization"):
            self.assertFalse(runner.summarize_xr(base + "\n" + failure)["evidenceVerified"])

    def test_completed_requires_acknowledged_shutdown_in_owned_live_process(self):
        report = {"supportedProxyLoaded": True, "lastLoggedPresentCount": 50, "exitCode": 0,
                  "benchmarkConfirmed": True, "serialRender": True, "xrRuntime": {"architecture": "x86"},
                  "benchmarkSummary": {"requestedConfigurationVerified": True, "serialRenderVerified": True},
                  "xrSummary": {"evidenceVerified": True}}
        self.assertFalse(runner.benchmark_completed(report))
        report["xrShutdownAcknowledged"] = True
        self.assertTrue(runner.benchmark_completed(report))


class XrRuntimeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.manifest = self.directory / "runtime.json"
        self.library = self.directory / "runtime.dll"
        self.manifest.write_text(json.dumps({"runtime": {"library_path": "runtime.dll"}}))
        data = bytearray(512)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 0x3c, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<H", data, 68, 0x14c)
        struct.pack_into("<H", data, 84, 224)
        struct.pack_into("<H", data, 86, 0x2000)
        struct.pack_into("<H", data, 88, 0x10b)
        self.library.write_bytes(data)

    def test_valid_relative_library_is_resolved_without_changing_files(self):
        before = {path: path.read_bytes() for path in (self.manifest, self.library)}
        result = runner.preflight_xr_manifest(self.manifest)
        self.assertEqual(result["architecture"], "x86")
        self.assertEqual(Path(result["library"]), self.library)
        self.assertEqual(len(result["librarySha256"]), 64)
        self.assertEqual(before, {path: path.read_bytes() for path in before})

    def test_bad_pe_headers_x64_non_dll_and_pe32plus_fail(self):
        original = self.library.read_bytes()
        for offset, value in ((0, b"NO"), (64, b"PX\0\0"), (68, struct.pack("<H", 0x8664)),
                              (86, b"\0\0"), (88, struct.pack("<H", 0x20b)),
                              (0x3c, struct.pack("<I", 0xffffffff))):
            data = bytearray(original)
            data[offset:offset + len(value)] = value
            self.library.write_bytes(data)
            with self.subTest(offset=offset), self.assertRaises(ValueError):
                runner.preflight_xr_manifest(self.manifest)

    def test_relative_manifest_and_malformed_library_path_fail(self):
        with self.assertRaises(ValueError):
            runner.preflight_xr_manifest(Path("runtime.json"))
        for runtime in (None, [], {}, {"library_path": 1}, {"library_path": ""}):
            self.manifest.write_text(json.dumps({"runtime": runtime}))
            with self.subTest(runtime=runtime), self.assertRaises(ValueError):
                runner.preflight_xr_manifest(self.manifest)

    def test_options_are_validated_before_any_filesystem_access(self):
        for flags in ([], ["--serial-render"], ["--scene-observer"],
                      ["--serial-render", "--scene-observer", "--replay-pair"]):
            with self.subTest(flags=flags), patch.object(sys, "argv", ["runner", "--run", "--xr-runtime", str(self.manifest), *flags]), \
                    patch.object(runner, "no_links") as read, patch.object(runner, "save_json") as write, \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                runner.main()
            self.assertEqual(error.exception.code, 2)
            read.assert_not_called()
            write.assert_not_called()

    def test_headset_minimum_duration_is_checked_before_filesystem_access(self):
        with patch.object(sys, "argv", ["runner", "--run", "--xr-runtime", str(self.manifest),
                                        "--serial-render", "--scene-observer", "--seconds", "119"]), \
                patch.object(runner, "no_links") as read, contextlib.redirect_stderr(io.StringIO()), \
                self.assertRaises(SystemExit):
            runner.main()
        read.assert_not_called()

    def test_explicit_child_environment_does_not_inherit_prototype_or_runtime(self):
        inherited = {name: "bad" for name in ("XR_RUNTIME_JSON", "DIRT3VR_XR_PROTOTYPE", "DIRT3VR_XR_STOP_FILE")}
        with patch.dict(os.environ, inherited):
            env = runner.diagnostic_environment(self.directory)
            self.assertFalse(set(inherited) & env.keys())
            env = runner.diagnostic_environment(self.directory, scene_observer=True, xr_runtime=self.manifest)
            self.assertEqual(env["XR_RUNTIME_JSON"], str(self.manifest))
            self.assertEqual(env["DIRT3VR_XR_PROTOTYPE"], "1")
            self.assertEqual(env["DIRT3VR_XR_STOP_FILE"], str(self.directory / "xr-stop.request"))
            self.assertNotIn("DIRT3VR_ENABLE_VR", env)

    def test_background_play_is_explicit_and_never_inherited(self):
        with patch.dict(os.environ, {"DIRT3VR_BACKGROUND_PLAY": "1"}):
            self.assertNotIn("DIRT3VR_BACKGROUND_PLAY", runner.diagnostic_environment(self.directory))
            env = runner.diagnostic_environment(self.directory, background_play=True)
            self.assertEqual(env["DIRT3VR_BACKGROUND_PLAY"], "1")
            self.assertNotIn("DIRT3VR_XR_PROTOTYPE", env)

    def test_stop_file_precedes_acknowledgement_and_exit_is_not_success(self):
        process = Mock()
        process.poll.return_value = None
        def log(_):
            self.assertTrue((self.directory / "xr-stop.request").is_file())
            return SHUTDOWN
        with patch.object(runner, "native_logs", side_effect=log):
            self.assertTrue(runner.request_xr_shutdown(process, self.directory, timeout=0))
            self.assertTrue(runner.request_xr_shutdown(process, self.directory, timeout=0, already_requested=True))
        process.poll.return_value = 0
        with patch.object(runner, "native_logs") as logs:
            self.assertFalse(runner.request_xr_shutdown(process, self.directory, timeout=0))
            logs.assert_not_called()

    def test_stop_timeout_is_not_acknowledged(self):
        process = Mock()
        process.poll.return_value = None
        with patch.object(runner, "native_logs", return_value=""):
            self.assertFalse(runner.request_xr_shutdown(process, self.directory, timeout=0))


if __name__ == "__main__":
    unittest.main()
