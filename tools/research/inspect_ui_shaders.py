"""Read DiRT3's shipped PSSG UI shader identities without executing the game."""
from pathlib import Path
import argparse
import hashlib
import json
import struct


def inspect(path):
    data = path.read_bytes()
    assert data[:4] == b'PSSG'
    u32 = lambda at: struct.unpack_from('>I', data, at)[0]
    assert u32(4) + 8 == len(data)
    at = 16
    names, attributes = {}, {}

    def string():
        nonlocal at
        size = u32(at)
        at += 4
        value = data[at:at+size].decode('utf-8')
        at += size
        return value

    for _ in range(u32(12)):
        key = u32(at)
        at += 4
        names[key] = string()
        count = u32(at)
        at += 4
        for _ in range(count):
            aid = u32(at)
            at += 4
            attributes[aid] = string()
    programs = []

    def node(start, limit, program=''):
        kind, size, attr_size = u32(start), u32(start+4), u32(start+8)
        end = start + 8 + size
        assert kind in names and start + 12 <= end <= limit
        cursor, attr_end = start + 12, start + 12 + attr_size
        assert attr_end <= end
        fields = {}
        while cursor < attr_end:
            aid, count = u32(cursor), u32(cursor+4)
            cursor += 8
            assert aid in attributes and cursor + count <= attr_end
            fields[attributes[aid]] = data[cursor:cursor+count]
            cursor += count
        assert cursor == attr_end
        if names[kind] == 'SHADERPROGRAM':
            value = fields['id']
            count = struct.unpack_from('>I', value)[0]
            assert count == len(value)-4
            program = value[4:].decode('utf-8').rstrip('\0')
        if names[kind] == 'SHADERPROGRAMCODEBLOCK':
            blob = data[cursor:end]
            assert blob[:4] == b'DXBC'
            size, count = struct.unpack_from('<II', blob, 24)
            assert size == len(blob) and 0 < count < 64
            stage = None
            for i in range(count):
                offset = struct.unpack_from('<I', blob, 32+4*i)[0]
                assert offset+8 <= size
                if blob[offset:offset+4] in (b'SHDR', b'SHEX'):
                    stage = struct.unpack_from('<I', blob, offset+8)[0] >> 16
            assert stage is not None and program
            fnv = 14695981039346656037
            for byte in blob:
                fnv = ((fnv ^ byte)*1099511628211) & 0xffffffffffffffff
            programs.append({'name': program, 'stage': stage, 'offset': cursor,
                             'size': size, 'fnv64': f'{fnv:016x}'})
        else:
            while cursor < end:
                # Non-container leaf data is opaque. Shader program code must
                # arrive through the typed code-block node above.
                if end-cursor < 12 or u32(cursor) not in names:
                    break
                cursor = node(cursor, end, program)
        return end

    assert node(at, len(data)) == len(data)
    vertex = [p for p in programs if p['stage'] == 1]
    # The on-screen display has both ui_2d_osd_* and ui_osd_2d_* programs.
    is_2d = lambda name: '.ui_2d_' in name or '.ui_osd_2d_' in name
    two_d = {p['fnv64'] for p in vertex if is_2d(p['name'])}
    other = {p['fnv64'] for p in vertex if not is_2d(p['name'])}
    return {'source': str(path), 'sha256': hashlib.sha256(data).hexdigest(),
            'programs': programs, 'twoDimensionalVertexHashes': sorted(two_d),
            'exclusiveTwoDimensionalVertexHashes': sorted(two_d-other),
            'sharedWithOtherUiHashes': sorted(two_d & other)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = inspect(args.input)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k: v if not isinstance(v, list) else len(v) for k, v in result.items()}))
