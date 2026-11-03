"""Light streaks and lens flares off: DiRT 3's post-processing effects file (ProJict #3c6d6ffa).

The glare around the sun isn't bloom (see bloom.py and docs/research/bloom.md) but two other
ParameterGroups of the same Effects in postprocess/effects.xml:

- Streaks: multiplier_0, multiplier_1 and multiplier_2 weight three anamorphic streaks (in
  Aspen 1.27, 1.16 and 4.0, the last one blue: the horizontal line through the sun). 50 effects
  set them, race_base among them (0.4, 0.4, 1.5).
- LensFlares: multiplier, the overall strength of the flare chain next to the per-element
  multiplierAndThreshold_* vectors (zeroing it removed every flare in run 20261103-204434).
  61 effects have it, 55 non-zero.

Both are screen-space effects, so in VR each eye draws its own streak and flare. Zeroing these
four values turns them off and leaves thresholds, colours, positions and every other group
alone. The tracks/generic effect files have neither group.

The file is Ego binary XML: new values are appended to the string table and only the changed
elements point at them (seat_cameras / motion_blur), so every other byte stays as shipped.

Usage: python tools/glare.py <effects.xml>   (lists the streak and flare multipliers)
"""
from __future__ import annotations

from pathlib import Path
import math
import sys

from motion_blur import VALUE, ZERO, _named, _set_text
from seat_cameras import BinXml, decode, encode

PARAMETERS = {"Streaks": ("multiplier_0", "multiplier_1", "multiplier_2"), "LensFlares": ("multiplier",)}


def multipliers(document: BinXml) -> list[tuple[str, str, str, int]]:
    """(effect, group, parameter, element) for every streak and flare multiplier, in file order."""
    if document.name(0) != "PostProcessEffects":
        raise ValueError("Not a postprocess effects file")
    found = []
    for effect, effect_name in _named(document, 0, "Effect"):
        for group, group_name in _named(document, effect, "ParameterGroup"):
            for param, param_name in _named(document, group, "Param"):
                if param_name in PARAMETERS.get(group_name, ()):
                    found.append((effect_name, group_name, param_name, param))
    return found


def value(document: BinXml, element: int) -> float:
    index = document.elements[element][1]
    match = VALUE.fullmatch(document.string(index) if index > 0 else "")
    number = float(match.group(2)) if match else math.nan
    if not math.isfinite(number):
        raise ValueError("Unexpected streak or flare value")
    return number


def disable_glare(data: bytes) -> bytes:
    """Set every Streaks and LensFlares multiplier to zero; zero ones keep their bytes."""
    document = decode(data)
    found = multipliers(document)
    if not found:
        raise ValueError("No streak or flare multipliers in this effects file")
    for _, _, _, element in found:
        if value(document, element) == 0:
            continue
        match = VALUE.fullmatch(document.string(document.elements[element][1]))
        _set_text(document, element, match.group(1) + ZERO + match.group(3))
    return encode(document)


def listing(data: bytes) -> list[tuple[str, str, str, float]]:
    document = decode(data)
    return [(effect, group, name, value(document, element)) for effect, group, name, element in multipliers(document)]


if __name__ == "__main__":
    for effect, group, name, number in listing(Path(sys.argv[1]).read_bytes()):
        print(f"{effect}\t{group}\t{name}\t{number:g}")
