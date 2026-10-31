"""Verify the two DiRT3 startup logo edits offline; never write installed files."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import xml.etree.ElementTree as ET
from inspect_cameras import decode_xml

ORIGINAL = '353f2b8fd00a8b26d47c74c446f07f1ec1ae38cf497482b426c0547bf2159996'


def inspect(game, output):
    data = (game / 'system/states.bin').read_bytes()
    assert hashlib.sha256(data).hexdigest() == ORIGINAL
    boot = ET.fromstring((game / 'system/boot_data.xml').read_bytes())
    assert len(boot.findall("./xml[@userdata='states'][@filename='/data/system/states.bin']")) == 1
    before = decode_xml(data)
    cursor = 16
    chunks = []
    for _ in range(4):
        size = struct.unpack_from('<I', data, cursor + 4)[0]
        chunks.append((cursor + 8, size))
        cursor += size + 8
    assert cursor == len(data)
    start, size = chunks[0]
    strings = data[start:start+size-1].decode('utf-8').split('\0')
    element_start, element_size = chunks[2]
    attribute_start, attribute_size = chunks[3]
    elements = list(struct.iter_unpack('<6i', data[element_start:element_start+element_size]))
    attributes = list(struct.iter_unpack('<2i', data[attribute_start:attribute_start+attribute_size]))
    patched = bytearray(data)
    edits, nodes = [], []

    def word(offset, value):
        original = struct.unpack_from('<i', data, offset)[0]
        edits.append(dict(offset=offset, original=original, replacement=value))
        struct.pack_into('<i', patched, offset, value)

    for identifier, filename in [('sting_video', r'video\sting.bik'), ('amd_sting_video', r'video\AMD_sting.bik')]:
        matches = []
        for i, (tag, value, count, first, children, child_first) in enumerate(elements):
            if strings[tag] != 'StateVideoPreStart':
                continue
            props = [(strings[k], strings[v]) for k, v in attributes[first:first+count]]
            if ('id', identifier) in props:
                matches.append((i, count, first, children, props))
        assert len(matches) == 1
        i, count, first, children, props = matches[0]
        assert count == 4 and children == 0
        assert props == [('id', identifier), ('filename', filename), ('filename_us', filename), ('skippable', 'true')]
        # Attribute records are individually indexed; these four records belong
        # only to this node. Two unused records remain byte-identical afterward.
        for j, (_, _, other_count, other_first, _, _) in enumerate(elements):
            if j != i and other_count:
                assert not set(range(first, first+count)) & set(range(other_first, other_first+other_count))
        word(element_start + i*24, strings.index('StateWayPoint'))
        word(element_start + i*24 + 8, 2)
        word(attribute_start + (first+1)*8, strings.index('timeDelay'))
        word(attribute_start + (first+1)*8 + 4, strings.index('0.0'))
        nodes.append(dict(id=identifier, element=i, before=dict(props), after=dict(id=identifier, timeDelay='0.0')))
    expected = decode_xml(data)
    for identifier in ['sting_video', 'amd_sting_video']:
        node = expected.find(f".//StateVideoPreStart[@id='{identifier}']")
        node.tag = 'StateWayPoint'
        node.attrib.clear()
        node.attrib.update(id=identifier, timeDelay='0.0')
    after = decode_xml(patched)
    assert ET.tostring(expected) == ET.tostring(after), 'Unrelated semantic change'
    assert len(after.findall('.//ScreenInitialLegal')) == len(before.findall('.//ScreenInitialLegal')) == 1
    output.mkdir(parents=True, exist_ok=True)
    (output / 'states-original.bin').write_bytes(data)
    (output / 'states-patched.bin').write_bytes(patched)
    report = dict(original_sha256=ORIGINAL, patched_sha256=hashlib.sha256(patched).hexdigest(),
                  bytes=len(data), edits=edits, nodes=nodes, semantic_changes='exactly two named logo nodes',
                  game_executed=False, installed_files_changed=False)
    (output / 'evidence.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    inspect(args.game, args.output)
