import importlib.util
import pathlib
import struct
import sys

spec = importlib.util.spec_from_file_location('generate_exports', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
data = pathlib.Path(sys.argv[2]).read_bytes()
items = module.exports(data)
assert len(items) >= 2
defs, cpp = module.generate(items)
for index, (name, ordinal) in enumerate(items):
    assert f' @{ordinal}\n' in defs
    assert f'"{name}"' in cpp
assert 'D3D11CreateDevice=ProxyCreateDevice' in defs
assert 'D3D11CreateDeviceAndSwapChain=ProxyCreateDeviceAndSwapChain' in defs
assert 'DiRT3VRGetStatus' in defs
assert 'pushfd' in cpp and 'pushad' in cpp and 'popad' in cpp and 'popfd' in cpp
for invalid in (b'not a PE', bytes(len(data))):
    try:
        module.exports(invalid)
        raise AssertionError('Invalid PE was accepted')
    except (ValueError, struct.error):
        pass
wrong_machine = bytearray(data)
pe = struct.unpack_from('<I', wrong_machine, 60)[0]
struct.pack_into('<H', wrong_machine, pe + 4, 0x8664)
try:
    module.exports(wrong_machine)
    raise AssertionError('x64 DLL was accepted')
except ValueError:
    pass
print(f'Validated export generator against {len(items)} actual Windows exports')
