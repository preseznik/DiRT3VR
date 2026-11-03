"""Check a portable package and its ZIP without executing any payload."""
import hashlib
import json
from pathlib import Path
import struct
import sys
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify(folder):
    root = folder.resolve(strict=True)
    manifest = json.loads((root / 'package.json').read_text(encoding='utf-8'))
    files = {p.relative_to(root).as_posix(): p for p in root.rglob('*') if p.is_file()}
    assert set(files) == set(manifest['files']) | {'package.json'}, 'Package inventory differs'
    for name, expected in manifest['files'].items():
        assert sha(files[name].read_bytes()) == expected, f'Changed payload: {name}'
    for name, machine, dll in [('DiRT3VR.exe', 0x8664, False),
                                ('payload/d3d11.dll', 0x14c, True),
                                ('payload/DiRT3VR.RuntimeProbe.exe', 0x14c, False),
                                ('payload/driving_input.dll', 0x8664, True)]:
        data = files[name].read_bytes()
        assert data[:2] == b'MZ', name
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        assert data[pe:pe+4] == b'PE\0\0', name
        assert struct.unpack_from('<H', data, pe+4)[0] == machine, name
        assert bool(struct.unpack_from('<H', data, pe+22)[0] & 0x2000) == dll, name
    capabilities = json.loads(files['runtime-capabilities.json'].read_text(encoding='utf-8'))
    assert capabilities['stereoVerified'] is False
    assert capabilities['experimentalInteractive'] is True
    assert capabilities['runtimes'] == ['vdxr', 'steamvr', 'other']
    assert all('test' not in name.lower() and not name.endswith(('.cpp', '.vb', '.pdb')) for name in files)
    archive = root.parent / f"DiRT3VR-{manifest['version']}.zip"
    with zipfile.ZipFile(archive) as zipped:
        actual = {entry.filename: entry for entry in zipped.infolist() if not entry.is_dir()}
        expected = {f'{root.name}/{name}' for name in files}
        assert set(actual) == expected and len(actual) == len(files), 'Archive inventory differs'
        for name, path in files.items():
            assert zipped.read(f'{root.name}/{name}') == path.read_bytes(), f'Archive content differs: {name}'
    # The Inno Setup installer is built from the same staged folder.
    setup = root.parent / f"DiRT3VR-{manifest['version']}-Setup.exe"
    installer = setup.read_bytes()
    pe = struct.unpack_from('<I', installer, 0x3c)[0]
    assert installer[:2] == b'MZ' and installer[pe:pe+4] == b'PE\0\0', 'Installer is not a Windows executable'
    sums = dict(reversed(line.split('  ', 1)) for line in
                (root.parent / 'SHA256SUMS.txt').read_text(encoding='ascii').splitlines() if line)
    assert sums == {archive.name: sha(archive.read_bytes()), setup.name: sha(installer)}, 'SHA256SUMS.txt differs'
    result = dict(schema=1, version=manifest['version'], payloadFiles=len(files),
                  nativeSha256=sha(files['payload/d3d11.dll'].read_bytes()),
                  archiveSha256=sha(archive.read_bytes()), archiveBytes=archive.stat().st_size,
                  installerSha256=sha(installer), installerBytes=len(installer),
                  stereoVerified=False, interactiveLiveAcceptance=False)
    (root.parent / 'validation.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    verify(Path(sys.argv[1]))
