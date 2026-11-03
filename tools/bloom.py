"""Bloom off: DiRT 3's post-processing effect files (ProJict #3c6d6ffa).

DiRT 3 has no bloom setting (research #f89609d3), but its bloom strength is data: every
post-processing Effect may carry a ParameterGroup name=BloomFromDsX4 whose Params
mixMedium, mixLarge, mixHuge and mixStreak set how much of each blurred bright-pass level is
added back (the same group DiRT2VR zeroes for its bloom option). In the supported install
60 of the 61 BloomFromDsX4 effects of postprocess/effects.xml have non-zero mixes (54 per
track and time of day, plus the finish, flashback, outbreak and message-box effects;
race_base is already zero), and
tracks/generic/effects.xml and night_effects.xml one each.
Thresholds and the other groups are left alone.

postprocess/effects.xml and tracks/generic/night_effects.xml are Ego binary XML (see
seat_cameras / motion_blur: new values are appended to the string table and only the
changed elements point at them, so every other byte stays as shipped).
tracks/generic/effects.xml is text XML; there only the numbers inside the BloomFromDsX4
mix Params change, keeping the file's whitespace and line endings.

Usage: python tools/bloom.py <effects.xml>   (lists the bloom mixes)
"""
from __future__ import annotations

from pathlib import Path
import math
import re
import sys

from motion_blur import VALUE, ZERO, _named, _set_text
from seat_cameras import BinXml, decode, encode

GROUP = "BloomFromDsX4"
MIXES = ("mixMedium", "mixLarge", "mixHuge", "mixStreak")
BINARY = b'\x1a"Rr'
TEXT_GROUP = re.compile(r'(<ParameterGroup name="BloomFromDsX4">)(.*?)(</ParameterGroup>)', re.S)
TEXT_MIX = re.compile(r'(<Param name="(?:mixMedium|mixLarge|mixHuge|mixStreak)">\s*)(-?\d+(?:\.\d+)?)(\s*</Param>)')


def mixes(document: BinXml) -> list[tuple[str, str, int]]:
    """(effect, parameter, element) for every bloom mix of a binary effects file, in file order."""
    if document.name(0) != "PostProcessEffects":
        raise ValueError("Not a postprocess effects file")
    found = []
    for effect, effect_name in _named(document, 0, "Effect"):
        for group, group_name in _named(document, effect, "ParameterGroup"):
            if group_name != GROUP:
                continue
            for param, param_name in _named(document, group, "Param"):
                if param_name in MIXES:
                    found.append((effect_name, param_name, param))
    return found


def value(document: BinXml, element: int) -> float:
    index = document.elements[element][1]
    match = VALUE.fullmatch(document.string(index) if index > 0 else "")
    number = float(match.group(2)) if match else math.nan
    if not math.isfinite(number):
        raise ValueError("Unexpected bloom value")
    return number


def _disable_binary(data: bytes) -> bytes:
    document = decode(data)
    found = mixes(document)
    if not found:
        raise ValueError("No bloom mixes in this effects file")
    for _, _, element in found:
        if value(document, element) == 0:
            continue
        match = VALUE.fullmatch(document.string(document.elements[element][1]))
        _set_text(document, element, match.group(1) + ZERO + match.group(3))
    return encode(document)


def _disable_text(data: bytes) -> bytes:
    text = data.decode("utf-8")
    if "<PostProcessEffects>" not in text:
        raise ValueError("Not a postprocess effects file")
    seen = 0

    def group(match: re.Match) -> str:
        nonlocal seen
        def mix(inner: re.Match) -> str:
            nonlocal seen
            seen += 1
            number = float(inner.group(2))
            return inner.group(0) if number == 0 else inner.group(1) + ZERO + inner.group(3)
        return match.group(1) + TEXT_MIX.sub(mix, match.group(2)) + match.group(3)

    result = TEXT_GROUP.sub(group, text)
    if not seen:
        raise ValueError("No bloom mixes in this effects file")
    return result.encode("utf-8")


def disable_bloom(data: bytes) -> bytes:
    """Set every BloomFromDsX4 mix to zero; already-zero mixes keep their bytes."""
    return _disable_binary(data) if data[:4] == BINARY else _disable_text(data)


def listing(data: bytes) -> list[tuple[str, str, float]]:
    """(effect, parameter, value) for every bloom mix, binary or text."""
    if data[:4] == BINARY:
        document = decode(data)
        return [(effect, name, value(document, element)) for effect, name, element in mixes(document)]
    rows = []
    for effect in re.finditer(r'<Effect name="([^"]*)"(.*?)</Effect>', data.decode("utf-8"), re.S):
        for group in TEXT_GROUP.finditer(effect.group(2)):
            for mix in re.finditer(r'<Param name="(mix\w+)">\s*(-?\d+(?:\.\d+)?)', group.group(2)):
                if mix.group(1) in MIXES:
                    rows.append((effect.group(1), mix.group(1), float(mix.group(2))))
    return rows


if __name__ == "__main__":
    for effect, name, number in listing(Path(sys.argv[1]).read_bytes()):
        print(f"{effect}\t{name}\t{number:g}")
