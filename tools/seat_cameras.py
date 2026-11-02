"""Seat position edits for DiRT 3 car camera files (cars/models/<car>/cameras.xml).

The files are Ego binary XML: a string container (strings, NUL-padded to 16 bytes, plus one
offset per string), a 24-byte element table and an 8-byte attribute table. Edits
here change attribute values only: new values are appended to the string table and
the changed attributes point at them, so every other byte stays as the game shipped
it and an edit with no changes re-encodes the original exactly.

The cockpit view is the View with ident "head-cam" (type Head). Its Parameter
name="position" type="vector3" holds the car-local eye point in metres (x, y, z;
frx: 0.3460, 1.0680, -0.3170), written with four decimals like every other value.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
import struct

MAGIC = b'\x1a"Rr'
STRINGS_CONTAINER = b'\x17"Rr'
STRINGS = b'\x1d"Rr'
OFFSETS = b'\x1e"Rr'
ELEMENTS = b'\x1b"Rr'
ATTRIBUTES = b'\x1c"Rr'
MAX_BYTES = 1024 * 1024
# A seat offset is a small adjustment from the car's own eye point, in metres.
MAX_OFFSET = 0.5


@dataclass
class BinXml:
    strings: list[str]
    elements: list[list[int]]    # name, value, attribute count, first attribute, child count, first child
    attributes: list[list[int]]  # name, value (string indices)

    def string(self, index: int) -> str:
        if not 0 <= index < len(self.strings):
            raise ValueError("Invalid BinXml string index")
        return self.strings[index]

    def name(self, element: int) -> str:
        return self.string(self.elements[element][0])

    def attribute_slots(self, element: int) -> dict[str, int]:
        _, _, count, first, _, _ = self.elements[element]
        slots = {}
        for slot in range(first, first + count) if count else []:
            key = self.string(self.attributes[slot][0])
            if key in slots:
                raise ValueError("Duplicate BinXml attribute")
            slots[key] = slot
        return slots

    def attributes_of(self, element: int) -> dict[str, str]:
        return {key: self.string(self.attributes[slot][1]) for key, slot in self.attribute_slots(element).items()}

    def children(self, element: int) -> range:
        _, _, _, _, count, first = self.elements[element]
        return range(first, first + count) if count else range(0)

    def set_attribute(self, element: int, key: str, value: str) -> None:
        slot = self.attribute_slots(element).get(key)
        if slot is None:
            raise ValueError(f"Element has no attribute {key}")
        if self.string(self.attributes[slot][1]) == value:
            return
        if "\0" in value:
            raise ValueError("Attribute values cannot contain NUL")
        # Strings are shared between attributes, so a new value never edits an existing string.
        try:
            index = self.strings.index(value)
        except ValueError:
            index = len(self.strings)
            self.strings.append(value)
        self.attributes[slot][1] = index


def _chunk(data: bytes, cursor: int, signature: bytes) -> tuple[bytes, int]:
    if cursor + 8 > len(data) or data[cursor:cursor + 4] != signature:
        raise ValueError("Unexpected BinXml chunk")
    size = struct.unpack_from("<I", data, cursor + 4)[0]
    end = cursor + 8 + size
    if end > len(data):
        raise ValueError("Truncated BinXml chunk")
    return data[cursor + 8:end], end


def decode(data: bytes, max_bytes: int = MAX_BYTES) -> BinXml:
    if not 24 <= len(data) <= max_bytes or data[:4] != MAGIC or struct.unpack_from("<I", data, 4)[0] != len(data) - 8:
        raise ValueError("Not a supported BinXml file")
    if data[8:12] != STRINGS_CONTAINER:
        raise ValueError("Missing BinXml string container")
    raw, cursor = _chunk(data, 16, STRINGS)
    offsets_raw, cursor = _chunk(data, cursor, OFFSETS)
    if struct.unpack_from("<I", data, 12)[0] != cursor - 16 or len(offsets_raw) % 4:
        raise ValueError("Invalid BinXml string container")
    offsets = struct.unpack(f"<{len(offsets_raw) // 4}I", offsets_raw)
    strings = []
    position = 0
    for offset in offsets:
        end = raw.find(b"\0", offset)
        if offset != position or end < 0:
            raise ValueError("BinXml string offsets are not sequential")
        strings.append(raw[offset:end].decode("utf-8"))
        position = end + 1
    if raw[position:] != b"\0" * (len(raw) - position) or len(raw) % 16:
        raise ValueError("Unexpected BinXml string padding")
    elements_raw, cursor = _chunk(data, cursor, ELEMENTS)
    attributes_raw, cursor = _chunk(data, cursor, ATTRIBUTES)
    if cursor != len(data) or len(elements_raw) % 24 or len(attributes_raw) % 8:
        raise ValueError("Invalid BinXml element or attribute table")
    document = BinXml(strings, [list(e) for e in struct.iter_unpack("<6i", elements_raw)],
                      [list(a) for a in struct.iter_unpack("<2i", attributes_raw)])
    for name, value, attribute_count, first_attribute, child_count, first_child in document.elements:
        document.string(name)
        if (attribute_count < 0 or child_count < 0
                or (attribute_count and not 0 <= first_attribute <= len(document.attributes) - attribute_count)
                or (child_count and not 0 <= first_child <= len(document.elements) - child_count)):
            raise ValueError("Invalid BinXml table range")
    for key, value in document.attributes:
        document.string(key)
        document.string(value)
    return document


def encode(document: BinXml) -> bytes:
    raw = bytearray()
    offsets = []
    for value in document.strings:
        offsets.append(len(raw))
        raw += value.encode("utf-8") + b"\0"
    # All 63 stock files pad the strings with 0-15 NULs to a multiple of 16 bytes.
    raw += b"\0" * (-len(raw) % 16)
    strings = STRINGS + struct.pack("<I", len(raw)) + bytes(raw)
    offsets_chunk = OFFSETS + struct.pack("<I", 4 * len(offsets)) + struct.pack(f"<{len(offsets)}I", *offsets)
    elements = b"".join(struct.pack("<6i", *e) for e in document.elements)
    attributes = b"".join(struct.pack("<2i", *a) for a in document.attributes)
    body = (STRINGS_CONTAINER + struct.pack("<I", len(strings) + len(offsets_chunk)) + strings + offsets_chunk
            + ELEMENTS + struct.pack("<I", len(elements)) + elements
            + ATTRIBUTES + struct.pack("<I", len(attributes)) + attributes)
    return MAGIC + struct.pack("<I", len(body)) + body


@dataclass
class Node:
    """An element as a tree, for edits that add or move elements."""
    name: str
    value: str | None
    attributes: list[tuple[str, str]]
    children: list["Node"]

    def get(self, key: str) -> str | None:
        return next((v for k, v in self.attributes if k == key), None)


def to_tree(document: BinXml) -> Node:
    def build(element: int, depth: int) -> Node:
        if depth > 256:
            raise ValueError("BinXml tree too deep")
        name, value, _, _, _, _ = document.elements[element]
        attributes = [(document.string(document.attributes[slot][0]), document.string(document.attributes[slot][1]))
                      for slot in document.attribute_slots(element).values()]
        return Node(document.string(name), document.string(value) if value > 0 else None, attributes,
                    [build(child, depth + 1) for child in document.children(element)])
    return build(0, 0)


def from_tree(root: Node, strings: list[str]) -> BinXml:
    """Lay the tree out as the game's files do: depth-first, each element's children in one
    block allocated when the element is visited; attributes in visiting order; empty attribute
    ranges start at 0 and leaf child ranges at the next free element. Existing strings keep
    their indices; new ones are appended."""
    table = list(strings)
    index = {value: i for i, value in reversed(list(enumerate(table)))}

    def string(value: str) -> int:
        if value not in index:
            index[value] = len(table)
            table.append(value)
        return index[value]

    elements: list[list[int] | None] = [None]
    attributes: list[list[int]] = []

    def place(position: int, node: Node) -> None:
        first_attribute = len(attributes) if node.attributes else 0
        attributes.extend([string(k), string(v)] for k, v in node.attributes)
        first_child = len(elements)
        elements.extend([None] * len(node.children))
        elements[position] = [string(node.name), string(node.value) if node.value is not None else 0,
                              len(node.attributes), first_attribute, len(node.children), first_child]
        for offset, child in enumerate(node.children):
            place(first_child + offset, child)

    place(0, root)
    return BinXml(table, elements, attributes)


def views(document: BinXml) -> list[int]:
    """The View elements of the single ViewManager, in file order."""
    if document.name(0) != "VehicleViewDefinition" or len(document.children(0)) != 1:
        raise ValueError("Unexpected camera definition root")
    manager = document.children(0)[0]
    if document.name(manager) != "ViewManager":
        raise ValueError("Expected a ViewManager")
    return [child for child in document.children(manager) if document.name(child) == "View"]


def head_cam(document: BinXml) -> int:
    found = [v for v in views(document) if document.attributes_of(v).get("ident") == "head-cam"]
    if len(found) != 1 or document.attributes_of(found[0]).get("type") != "Head":
        raise ValueError("Expected exactly one Head view named head-cam")
    return found[0]


def head_position(document: BinXml) -> tuple[int, tuple[float, float, float]]:
    view = head_cam(document)
    found = [e for e in _descendants(document, view)
             if document.name(e) == "Parameter" and document.attributes_of(e).get("name") == "position"]
    if len(found) != 1:
        raise ValueError("Expected one head-cam position")
    attributes = document.attributes_of(found[0])
    if attributes.get("type") != "vector3" or set(attributes) != {"name", "type", "x", "y", "z"}:
        raise ValueError("Unexpected head-cam position parameter")
    values = tuple(float(attributes[axis]) for axis in "xyz")
    if not all(math.isfinite(v) for v in values):
        raise ValueError("Non-finite head-cam position")
    return found[0], values


def _descendants(document: BinXml, element: int):
    for child in document.children(element):
        yield child
        yield from _descendants(document, child)


def offset_seat(data: bytes, sideways: float, up: float, forward: float) -> bytes:
    """Move the cockpit eye point by (sideways, up, forward) metres along the car's x, y, z."""
    for value in (sideways, up, forward):
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or abs(value) > MAX_OFFSET:
            raise ValueError(f"Seat offsets must be finite and within ±{MAX_OFFSET} m")
    document = decode(data)
    element, position = head_position(document)
    for axis, base, delta in zip("xyz", position, (sideways, up, forward)):
        document.set_attribute(element, axis, f"{base + delta:.4f}")
    return encode(document)


def start_in_cockpit(data: bytes) -> bytes:
    """Research: hide the views listed before head-cam, so the car starts in the cockpit view."""
    document = decode(data)
    cockpit = head_cam(document)
    for view in views(document):
        if view == cockpit:
            break
        if document.attributes_of(view).get("hidden") == "false":
            document.set_attribute(view, "hidden", "true")
    return encode(document)
