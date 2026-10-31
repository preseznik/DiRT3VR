"""The no-headset failure path must remain parseable and never create a session."""
import json
import pathlib
import subprocess
import sys
import tempfile


def run(*args):
    result = subprocess.run([sys.argv[1], *args], text=True, capture_output=True, timeout=30)
    assert result.returncode != 0, result.stdout
    value = json.loads(result.stdout)
    assert value['schema'] == 1 and value['success'] is False
    assert value['stereo'] is False and value['sessionCreated'] is False
    assert value['architecture'] == 'x86' and value['error']
    return value


run('--unknown')
run('--runtime-json', 'relative-missing-runtime.json')
with tempfile.TemporaryDirectory(prefix='DiRT3VR-runtime-test-') as folder:
    manifest = pathlib.Path(folder) / 'runtime.json'
    manifest.write_text(json.dumps({'file_format_version': '1.0.0', 'runtime': {'library_path': str(pathlib.Path(folder) / 'absent.dll')}}))
    value = run('--runtime-json', str(manifest))
    assert value['operation'] == 'enumerateExtensions', value
print('Runtime probe failure JSON tests passed')
