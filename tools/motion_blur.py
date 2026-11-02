"""Motion blur off for VR sessions: DiRT 3's postprocess/effects.xml.

In VR, head movement is camera movement, so DiRT 3's motion blur smears whatever is
closest to the camera; a low pre-race camera turns the nearby ground into streaks
(ProJict #30d34783). DiRT2VR sets every MotionBlur blurLength to zero for VR sessions.

The file is Ego binary XML (see seat_cameras): PostProcessEffects > Effect name=... >
ParameterGroup name=MotionBlur > Param name=blurLength / frozenBlurLength, each value
being the element's text ("\\r\\n\\t\\t\\t6.000000\\r\\n\\t\\t"). disable_motion_blur
sets both lengths to 0.000000, keeping each value's surrounding whitespace. New
values are appended to the string table and only those elements point at them, so
every other byte stays as the game shipped it.

Usage: python tools/motion_blur.py <effects.xml>   (lists the motion blur lengths)
"""
from __future__ import annotations

from pathlib import Path
import math
import re
import sys

from seat_cameras import BinXml, decode, encode

LENGTHS = ("blurLength", "frozenBlurLength")
ZERO = "0.000000"
VALUE = re.compile(r"(\s*)(-?\d+(?:\.\d+)?)(\s*)")


def _named(document: BinXml, element: int, tag: str) -> list[tuple[int, str]]:
    return [(child, document.attributes_of(child).get("name", "")) for child in document.children(element)
            if document.name(child) == tag]


def lengths(document: BinXml) -> list[tuple[str, str, int]]:
    """(effect, parameter, element) for every motion blur length, in file order."""
    if document.name(0) != "PostProcessEffects":
        raise ValueError("Not a postprocess effects file")
    found = []
    for effect, effect_name in _named(document, 0, "Effect"):
        for group, group_name in _named(document, effect, "ParameterGroup"):
            if group_name != "MotionBlur":
                continue
            for param, param_name in _named(document, group, "Param"):
                if param_name in LENGTHS:
                    found.append((effect_name, param_name, param))
    return found


def value(document: BinXml, element: int) -> float:
    index = document.elements[element][1]
    match = VALUE.fullmatch(document.string(index) if index > 0 else "")
    number = float(match.group(2)) if match else math.nan
    if not math.isfinite(number):
        raise ValueError("Unexpected motion blur value")
    return number


def _set_text(document: BinXml, element: int, text: str) -> None:
    # Strings are shared between elements, so a new value never edits an existing string.
    try:
        index = document.strings.index(text)
    except ValueError:
        index = len(document.strings)
        document.strings.append(text)
    document.elements[element][1] = index


def disable_motion_blur(data: bytes) -> bytes:
    """Set every MotionBlur blurLength and frozenBlurLength to zero."""
    document = decode(data)
    found = lengths(document)
    if not any(name == "blurLength" for _, name, _ in found):
        raise ValueError("No motion blur lengths in this effects file")
    for _, _, element in found:
        if value(document, element) == 0:
            continue
        match = VALUE.fullmatch(document.string(document.elements[element][1]))
        _set_text(document, element, match.group(1) + ZERO + match.group(3))
    return encode(document)


if __name__ == "__main__":
    document = decode(Path(sys.argv[1]).read_bytes())
    for effect, name, element in lengths(document):
        print(f"{effect}\t{name}\t{value(document, element):g}")
