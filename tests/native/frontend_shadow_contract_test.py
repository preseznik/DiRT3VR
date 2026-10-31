"""Read-only native producer and shipped shader contract; never launch the game."""
import hashlib
import importlib.util
from pathlib import Path
import re
import struct
import sys

root, game = map(Path, sys.argv[1:])
pack = game.parent / 'dx11/shaderpack/shaderpack.pssg'
if not game.is_file() or not pack.is_file():
    print('Installed DiRT 3 binary or shaders unavailable')
    sys.exit(77)
data = game.read_bytes()
assert hashlib.sha256(data).hexdigest() == 'c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98'
pe = struct.unpack_from('<I', data, 60)[0]
sections = pe + 24 + struct.unpack_from('<H', data, pe + 20)[0]


def at(rva):
    for i in range(struct.unpack_from('<H', data, pe + 6)[0]):
        size, address, raw_size, raw = struct.unpack_from('<IIII', data, sections + 40*i + 8)
        if address <= rva < address + min(size, raw_size):
            return raw + rva - address
    raise AssertionError(f'Unmapped RVA {rva:x}')


state = (root / 'src/frontend_shadow_state.cpp').read_text(encoding='utf-8')
for name, rva in [('ProducerEntry', 0x17cc20), ('ProducerCall', 0x1805eb), ('MatrixStores', 0x17d0e2)]:
    body = re.search(name + r'\[\]=\{([^}]+)\}', state).group(1)
    expected = bytes(int(x, 16) for x in body.split(','))
    assert data[at(rva):at(rva)+len(expected)] == expected, name
assert data[at(0x17d35d):at(0x17d35d)+3] == b'\xc2\x04\x00', 'producer ABI'
assert 0x1805f0 + struct.unpack_from('<i', data, at(0x1805eb)+1)[0] == 0x17cc20
assert b'feParticleCameraInverse\0' == data[at(0xaacf30):at(0xaacf30)+24]

spec = importlib.util.spec_from_file_location('shader_pack', root / 'tools/research/inspect_ui_shaders.py')
parser = importlib.util.module_from_spec(spec)
spec.loader.exec_module(parser)
inventory = parser.inspect(pack)
assert inventory['sha256'] == '644c069f0bc38faa8e64f53baaf7bd7af40638fe366d63edb133f043c55d9d46'
shader_source = (root / 'src/frontend_shadow.h').read_text(encoding='utf-8')
hashes = set(re.findall(r'0x([0-9a-f]{16})ull', shader_source))
assert len(hashes) == 4
blobs = {p['fnv64']: p for p in inventory['programs'] if p['fnv64'] in hashes}
assert blobs.keys() == hashes
pack_data = pack.read_bytes()
for key, program in blobs.items():
    blob = pack_data[program['offset']:program['offset']+program['size']]
    if program['stage'] == 1:
        assert key == '4b2b0f44adfbf5bb'
        continue
    assert program['stage'] == 0
    # DXBC reflection is embedded in the shipped blob. Check actual field
    # size and offset, not a handwritten copy of the runtime structure.
    chunks = [struct.unpack_from('<I', blob, 32+4*i)[0] for i in range(struct.unpack_from('<I', blob, 28)[0])]
    location = next(i for i in chunks if blob[i:i+4] == b'RDEF')
    reflection = blob[location+8:location+8+struct.unpack_from('<I', blob, location+4)[0]]
    u32 = lambda pos: struct.unpack_from('<I', reflection, pos)[0]
    string = lambda pos: reflection[pos:reflection.index(b'\0', pos)].decode('ascii')
    found = False
    for i in range(u32(0)):
        cb = u32(4) + i*24
        if string(u32(cb)) != '$Globals':
            continue
        assert u32(cb+12) == 240
        for j in range(u32(cb+4)):
            variable = u32(cb+8) + j*40  # Shader model 5 variable descriptor.
            if string(u32(variable)) == 'feParticleCameraInverse':
                assert (u32(variable+4), u32(variable+8)) == (48, 64)
                found = True
    assert found, program['name']
print('Verified fingerprint, producer ABI/call/stores and all three frontend shader matrix layouts')
