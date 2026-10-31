"""Read-only stock BinXml camera inventory. No game execution or source writes.

The chunk layout is independently bounds-checked against the existing
EgoEngineLibrary XmlFile/BinaryXmlElement reader used by DiRT2VR's tools.
Only the observed little-endian BinXml format and ordinary XML are supported.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import xml.etree.ElementTree as ET


def decode_xml(data: bytes) -> ET.Element:
    if data[:4] != b'\x1a"Rr':
        return ET.fromstring(data)
    if len(data) < 24 or struct.unpack_from("<I", data, 4)[0] != len(data) - 8:
        raise ValueError("Invalid BinXml file length")
    if data[8:12] != b'\x17"Rr':
        raise ValueError("Invalid BinXml string-container signature")
    cursor = 16

    def chunk(signature: bytes | None = None) -> bytes:
        nonlocal cursor
        if cursor + 8 > len(data):
            raise ValueError("Truncated BinXml chunk")
        if signature is not None and data[cursor:cursor + 4] != signature:
            raise ValueError("Unexpected BinXml chunk signature")
        size = struct.unpack_from("<I", data, cursor + 4)[0]
        start = cursor + 8
        cursor = start + size
        if cursor > len(data):
            raise ValueError("Truncated BinXml chunk payload")
        return data[start:cursor]

    raw_strings = chunk(b'\x1d"Rr')
    if not raw_strings.endswith(b"\0"):
        raise ValueError("Unterminated BinXml string table")
    strings = [value.decode("utf-8") for value in raw_strings[:-1].split(b"\0")]
    offsets = chunk(b'\x1e"Rr')
    if len(offsets) % 4 or cursor - 16 != struct.unpack_from("<I", data, 12)[0]:
        raise ValueError("Invalid BinXml string-container length")
    raw_elements = chunk()
    raw_attributes = chunk()
    if cursor != len(data) or len(raw_elements) % 24 or len(raw_attributes) % 8:
        raise ValueError("Invalid BinXml element/attribute tables")
    elements = list(struct.iter_unpack("<6i", raw_elements))
    attributes = list(struct.iter_unpack("<2i", raw_attributes))
    visited = set()

    def string(index):
        if not 0 <= index < len(strings):
            raise ValueError("Invalid BinXml string index")
        return strings[index]

    def build(index):
        if index in visited or not 0 <= index < len(elements):
            raise ValueError("Invalid/cyclic BinXml element tree")
        visited.add(index)
        name, value, attr_count, attr_start, child_count, child_start = elements[index]
        if (attr_count < 0 or child_count < 0 or (attr_count and not 0 <= attr_start <= len(attributes) - attr_count)
                or (child_count and not 0 <= child_start <= len(elements) - child_count)):
            raise ValueError("Invalid BinXml table range")
        result = ET.Element(string(name))
        for attribute in attributes[attr_start:attr_start + attr_count] if attr_count else []:
            key, val = map(string, attribute)
            if key in result.attrib:
                raise ValueError("Duplicate BinXml attribute")
            result.set(key, val)
        for child in range(child_start, child_start + child_count):
            result.append(build(child))
        if value > 0 and child_count == 0:
            result.text = string(value)
        return result

    root = build(0)
    if len(visited) != len(elements):
        raise ValueError("Unreachable BinXml elements")
    return root


def inventory(path: Path) -> dict:
    data = path.read_bytes()
    root = decode_xml(data)
    names = {"nearPlane", "farPlane", "fov", "highResInterior", "DisableDriverHead", "DisableDriver",
             "DisableCoDriver", "offset", "position", "target", "yaw", "pitch", "roll", "tilt"}
    return {"path": str(path.resolve()), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
            "format": "BinXml" if data[:4] == b'\x1a"Rr' else "XML", "root": root.tag,
            "views": [{"attributes": view.attrib,
                       "parameters": [parameter.attrib for parameter in view.findall(".//Parameter")
                                      if parameter.get("name") in names]}
                      for view in root.findall(".//View")],
            "namedNearPlanes": [element.attrib for element in root.iter()
                                if element.get("name") == "nearPlane"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", type=Path)
    args = parser.parse_args()
    print(json.dumps([inventory(path) for path in args.inputs], indent=2))


if __name__ == "__main__":
    main()
