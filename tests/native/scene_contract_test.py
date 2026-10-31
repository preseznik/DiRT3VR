"""Verify passive observer guards against disk bytes; never load/execute the game."""
import hashlib
import pathlib
import re
import struct
import sys

source = pathlib.Path(sys.argv[1])
game = pathlib.Path(sys.argv[2])
if not game.is_file():
    print('Supported local DiRT 3 executable unavailable; signature test skipped')
    sys.exit(77)
data = game.read_bytes()
diagnostics = (source / 'diagnostics.cpp').read_text()
expected_hash = re.search(r'SupportedSha256\[\] = "([0-9a-f]{64})"', diagnostics).group(1)
assert hashlib.sha256(data).hexdigest() == expected_hash, 'Executable fingerprint differs from production guard'
pe = struct.unpack_from('<I', data, 60)[0]
assert data[:2] == b'MZ' and data[pe:pe+4] == b'PE\0\0'
assert struct.unpack_from('<H', data, pe+4)[0] == 0x14c, 'Expected x86 executable'
optional = pe + 24
sections = optional + struct.unpack_from('<H', data, pe+20)[0]


def offset(rva):
    for index in range(struct.unpack_from('<H', data, pe+6)[0]):
        section = sections + index * 40
        size, address, raw_size, raw = struct.unpack_from('<IIII', data, section+8)
        if address <= rva < address + min(size, raw_size):
            return raw + rva - address
    raise AssertionError(f'RVA {rva:x} does not map to file bytes')


observer = (source / 'scene_observer.cpp').read_text()


def rva(name, text=observer):
    return int(re.search(r'\b' + name + r' = (0x[0-9a-f]+)', text).group(1), 16)


for name in ('Outer', 'Inner', 'HistorySelect', 'HistoryInvalidate'):
    start = offset(rva(name + 'Rva'))
    initializer = re.search(name + r'Entry\[\] = \{([^}]+)\}', observer).group(1)
    expected = bytes(int(value, 16) for value in initializer.split(','))
    assert len(expected) >= 8 and data[start:start+len(expected)] == expected, name + ' loaded-byte guard differs'
for slot, name in ((0x68, 'HistorySelect'), (0x60, 'HistoryInvalidate')):
    target = struct.unpack_from('<I', data, offset(rva('VelocityTableRva') + slot))[0]
    assert target == 0x400000 + rva(name + 'Rva'), name + ' vtable target differs'
for caller, target in (('MainReturnRva', 'OuterRva'), ('FirstInnerReturnRva', 'InnerRva'), ('SecondInnerReturnRva', 'InnerRva')):
    return_rva = rva(caller)
    call = offset(return_rva - 5)
    assert data[call] == 0xe8, caller + ' is not a direct relative call'
    destination = return_rva + struct.unpack_from('<i', data, call + 1)[0]
    assert destination == rva(target), caller + ' calls a different target'
projection = (source / 'scene_projection.cpp').read_text()
for method, entry in (('Setup', 'Aligned'), ('Upload', 'Aligned'), ('Build', 'Aligned'), ('Copy', 'Copy'), ('StereoGetter', 'StereoGetter')):
    initializer = re.search(entry + r'Entry\[\] = \{([^}]+)\}', projection).group(1)
    expected = bytes(int(value, 16) for value in initializer.split(','))
    start = offset(rva(method + 'Rva', projection))
    assert data[start:start+len(expected)] == expected, method + ' projection guard differs'
for call_rva, target_name in ((0x584a8b, 'Setup'), (0x584adf, 'Setup'), (0x180914, 'Setup'),
                              (0x180932, 'Build'), (0x18093a, 'Copy')):
    call = offset(call_rva)
    assert data[call] == 0xe8
    assert call_rva + 5 + struct.unpack_from('<i', data, call+1)[0] == rva(target_name + 'Rva', projection)
for table, slot, target in (('DeviceTableRva', 0x148, 0x4a81f0), ('ContextTableRva', 0x40, 0x4ab360),
                            ('DeviceTableRva', 0xd0, 0x4a8ca0)):
    assert struct.unpack_from('<I', data, offset(rva(table, projection) + slot))[0] == 0x400000 + target
resolution = (source / 'scene_resolution.cpp').read_text(encoding='utf-8')
for name in ('Dimensions', 'Descriptor', 'Create', 'Window', 'Recreate', 'Resize'):
    initializer = re.search(name + r'Bytes\[\] = \{([^}]+)\}', resolution).group(1)
    expected = bytes(int(value, 16) for value in initializer.split(','))
    start = offset(rva(name + 'Rva', resolution))
    assert data[start:start+len(expected)] == expected, name + ' scene-size guard differs'
for name in ('Create', 'Recreate', 'Resize'):
    end = offset(rva(name + 'ReturnRva', resolution))
    assert data[end-2:end] in (b'\xff\xd0', b'\xff\xd2'), name + ' return is not the guarded indirect call'
print('Exact SHA-256, x86 architecture, scene/history/projection/visibility/resolution guards verified without executing game')
