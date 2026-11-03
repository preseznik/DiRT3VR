import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from run_benchmark import benchmark_completed, summarize_benchmark


class BenchmarkResultTests(unittest.TestCase):
    def summarize(self, parallel="false", route="route_0", *, serial_render=True, written="off", requested="off"):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.xml"
            path.write_text(f'''<benchmark directx_version="11">
              <track><settings name="aspen" route="{route}"><car name="frx"/></settings>
                <results samples="100"/></track>
              <hardware_settings_config><cpu><threadStrategy parallelUpdateRender="{parallel}"/></cpu>
                <graphics_card><directx forcedx9="false"/><stereo enabled="false"/>
                  <resolution width="1280" height="720" fullscreen="false" vsync="0" multisampling="{written}"/>
                </graphics_card></hardware_settings_config></benchmark>''')
            return summarize_benchmark(path, serial_render=serial_render, multisampling=requested)

    def test_requested_scene_and_configuration_are_verified(self):
        result = self.summarize()
        self.assertTrue(result["sceneConfirmed"])
        self.assertTrue(result["requestedConfigurationVerified"])
        self.assertTrue(result["serialRenderVerified"])

    def test_game_reenabled_parallel_render_is_reported_as_mismatch(self):
        result = self.summarize(parallel="true")
        self.assertTrue(result["sceneConfirmed"])
        self.assertFalse(result["requestedConfigurationVerified"])
        self.assertEqual(result["configurationMismatches"], ["cpu/threadStrategy/@parallelUpdateRender"])
        self.assertFalse(result["serialRenderVerified"])

    def test_requested_msaa_must_be_the_one_the_game_ran(self):
        self.assertTrue(self.summarize(written="4xmsaa", requested="4xmsaa")["requestedConfigurationVerified"])
        result = self.summarize(written="off", requested="2xmsaa")
        self.assertFalse(result["requestedConfigurationVerified"])
        self.assertEqual(result["configurationMismatches"], ["graphics_card/resolution/@multisampling"])
        self.assertEqual(result["observedConfiguration"]["graphics_card/resolution/@multisampling"], "off")

    def test_wrong_scene_is_not_benchmark_acceptance(self):
        result = self.summarize(route="route_9")
        self.assertFalse(result["sceneConfirmed"])
        self.assertFalse(result["requestedConfigurationVerified"])

    def test_default_diagnostic_does_not_claim_a_serial_requirement(self):
        result = self.summarize(parallel="true", serial_render=False)
        self.assertTrue(result["requestedConfigurationVerified"])
        self.assertFalse(result["serialRenderRequested"])
        self.assertFalse(result["serialRenderVerified"])

    def test_serial_run_cannot_complete_without_result_proving_false(self):
        report = {"supportedProxyLoaded": True, "lastLoggedPresentCount": 50,
                  "exitCode": 0, "forcedTermination": False, "benchmarkConfirmed": True,
                  "serialRender": True}
        self.assertFalse(benchmark_completed(report))
        report["benchmarkSummary"] = self.summarize(parallel="true", serial_render=False)
        self.assertFalse(benchmark_completed(report))
        report["benchmarkSummary"] = self.summarize()
        self.assertTrue(benchmark_completed(report))
        report["forcedTermination"] = True
        self.assertFalse(benchmark_completed(report))


if __name__ == "__main__":
    unittest.main()
