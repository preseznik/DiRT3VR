import contextlib
import io
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import run_benchmark as runner


FIRST_SCENE = 1733
SAMPLE_SCENES = tuple(FIRST_SCENE + offset for offset in runner.REPLAY_SAMPLE_OFFSETS)


def pair_line(scene, **overrides):
    sampled = int(scene in SAMPLE_SCENES)
    values = {"main": scene, "ready": 1, "restored": 1, "calls": 2, "sampled": sampled,
              "history_before": scene, "history_first": scene + 1, "history_second": scene + 1,
              "same_frame": 1, "vertices": "100/100" if sampled else "0/0",
              "primitives": "50/50" if sampled else "0/0", "pixels": "1000/1000" if sampled else "0/0",
              "present_enter": scene, "present_exit": scene, "stereo_verified": "false"}
    values.update(overrides)
    return "[12:34:56] Replay pair " + " ".join(f"{key}={value}" for key, value in values.items())


def write_capture(path):
    pixels = 1280 * 720 * 4
    header = struct.pack("<2sIHHI", b"BM", 54 + pixels, 0, 0, 54)
    header += struct.pack("<IiiHHIIiiII", 40, 1280, -720, 1, 32, 0, pixels, 0, 0, 0, 0)
    with path.open("xb") as stream:
        stream.write(header)
        stream.seek(54 + pixels - 1)
        stream.write(b"\0")


class ReplayEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        for scene in SAMPLE_SCENES:
            for eye in (0, 1):
                write_capture(self.directory / f"replay-{scene}-eye{eye}.bmp")
        self.lines = [pair_line(scene) for scene in range(FIRST_SCENE, FIRST_SCENE + 120)]

    def summarize(self, lines=None):
        return runner.summarize_replay("\n".join(self.lines if lines is None else lines), self.directory)

    def test_exact_bounded_pairs_and_six_complete_captures_are_required(self):
        summary = self.summarize()
        self.assertTrue(summary["evidenceVerified"])
        self.assertEqual(summary["pairCount"], 120)
        self.assertEqual(summary["validPairCount"], 120)
        self.assertEqual(summary["sampledScenes"], list(SAMPLE_SCENES))
        self.assertEqual(len(summary["captures"]), 6)
        self.assertFalse(summary["stereoVerified"])

    def test_missing_duplicate_and_reordered_pairs_are_not_acceptance(self):
        for lines in ([], self.lines[:-1], self.lines + self.lines[:1], list(reversed(self.lines))):
            with self.subTest(count=len(lines)):
                self.assertFalse(self.summarize(lines)["evidenceVerified"])

    def test_failed_state_and_inconsistent_frame_evidence_are_rejected(self):
        for field, value in (("ready", 0), ("restored", 0), ("calls", 1), ("sampled", 0),
                             ("history_before", 1), ("history_first", 1), ("history_second", 1),
                             ("same_frame", 0), ("present_exit", 1), ("pixels", "0/1000"),
                             ("primitives", "0/50"), ("stereo_verified", "true")):
            with self.subTest(field=field):
                lines = [pair_line(FIRST_SCENE, **{field: value}), *self.lines[1:]]
                self.assertFalse(self.summarize(lines)["evidenceVerified"])

    def test_history_counter_wrap_is_valid(self):
        lines = [pair_line(FIRST_SCENE, history_before=0xffffffff, history_first=0, history_second=0), *self.lines[1:]]
        self.assertTrue(self.summarize(lines)["evidenceVerified"])

    def test_any_refusal_or_failure_invalidates_otherwise_valid_evidence(self):
        for failure in ("Replay refused reason=guards", "Replay failed main=12000 reason=exception"):
            with self.subTest(failure=failure):
                result = self.summarize([*self.lines, failure])
                self.assertFalse(result["evidenceVerified"])
                self.assertIn(failure, result["errors"])

    def test_malformed_extra_pair_is_not_ignored(self):
        self.assertFalse(self.summarize([*self.lines, "Replay pair main=12345"])["evidenceVerified"])

    def test_missing_and_extra_capture_fail(self):
        capture = self.directory / f"replay-{FIRST_SCENE}-eye0.bmp"
        capture.unlink()
        self.assertFalse(self.summarize()["evidenceVerified"])
        write_capture(capture)
        write_capture(self.directory / "replay-99999-eye0.bmp")
        self.assertFalse(self.summarize()["evidenceVerified"])

    def test_truncated_capture_is_not_accepted_as_image(self):
        (self.directory / f"replay-{FIRST_SCENE}-eye0.bmp").write_bytes(b"BM")
        self.assertFalse(self.summarize()["evidenceVerified"])

    def test_wrong_capture_dimensions_are_rejected(self):
        with (self.directory / f"replay-{FIRST_SCENE}-eye0.bmp").open("r+b") as stream:
            stream.seek(18)
            stream.write(struct.pack("<i", 1920))
        self.assertFalse(self.summarize()["evidenceVerified"])


class ReplayOptInTests(unittest.TestCase):
    def test_both_prerequisites_are_checked_before_filesystem_access(self):
        for flags in ([], ["--serial-render"], ["--scene-observer"]):
            with self.subTest(flags=flags), patch.object(sys, "argv", ["runner", "--run", "--replay-pair", *flags]), \
                    patch.object(runner, "no_links") as read, patch.object(runner, "save_json") as write, \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                runner.main()
            self.assertEqual(error.exception.code, 2)
            read.assert_not_called()
            write.assert_not_called()

    def test_baseline_clears_inherited_replay_and_vr_flags(self):
        with patch.dict(os.environ, {"DIRT3VR_ENABLE_VR": "1", "DIRT3VR_SCENE_DIAGNOSTICS": "1",
                                     "DIRT3VR_REPLAY_DIAGNOSTICS": "1"}):
            env = runner.diagnostic_environment(Path("logs"))
            for name in ("DIRT3VR_ENABLE_VR", "DIRT3VR_SCENE_DIAGNOSTICS", "DIRT3VR_REPLAY_DIAGNOSTICS"):
                self.assertNotIn(name, env)
            env = runner.diagnostic_environment(Path("logs"), scene_observer=True, replay_pair=True)
            self.assertEqual(env["DIRT3VR_SCENE_DIAGNOSTICS"], "1")
            self.assertEqual(env["DIRT3VR_REPLAY_DIAGNOSTICS"], "1")
            self.assertNotIn("DIRT3VR_ENABLE_VR", env)

    def test_replay_run_needs_replay_evidence_as_well_as_benchmark(self):
        report = {"supportedProxyLoaded": True, "lastLoggedPresentCount": 50, "exitCode": 0,
                  "forcedTermination": False, "benchmarkConfirmed": True, "serialRender": True,
                  "replayPair": True, "benchmarkSummary": {"requestedConfigurationVerified": True,
                                                              "serialRenderVerified": True}}
        self.assertFalse(runner.benchmark_completed(report))
        report["replaySummary"] = {"evidenceVerified": False}
        self.assertFalse(runner.benchmark_completed(report))
        report["replaySummary"] = {"evidenceVerified": True}
        self.assertTrue(runner.benchmark_completed(report))


if __name__ == "__main__":
    unittest.main()
