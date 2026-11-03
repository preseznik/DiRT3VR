"""Startup skip: DiRT 3's system/states.bin (ProJict #0ed1f030).

DiRT 3's startup goes: AMD and Codemasters logo movies, the garage load, the copyright screen
(ScreenInitialLegal id=screen_initial_legal), a fade from white, the Press Start camera flight
(StateGarageZone id=press_start_cutscene, camera group press_start, zone 1) and Press Start. The
launcher's "Skip startup logo movies" already turns the two StateVideoPreStart nodes into
StateWayPoint nodes that move on at once (tools/research/inspect_startup_movies.py,
launcher/StartupMovies.vb); iztok also wants the copyright screen and the camera flight gone
(2026-11-03). The flow graph (system/flow.bin) links states by id, so each node keeps its id and
only its type and second attribute change: StateWayPoint id=<same> timeDelay=0.0. Attribute
records past the second stay in the file unused, byte-identical, as in the movie edit.

Press Start stays: it binds the player's controller and loads the profile, and autosave depends on
it. The fade from white stays too (StatePostProcessTransition overlay=off): skipping it could leave
the white overlay on, and it is short.

In the game (2026-11-03, normal starts on PC4080S) only "movies" works. "legal" crashes DiRT 3 right
after loading (access violation, runs 20261103-221042 and -230706): ScreenInitialLegal is a screen,
not a state, and can't become a waypoint. "cutscene" leaves a black screen with no Press Start (run
20261103-230726): the camera flight sets up the garage view Press Start draws on. Both parts stay
here for research only; frontend/durations.xml's intro_part_1/2 (4 s and 3 s) are the next lead.

Usage: python tools/startup_skip.py <states.bin>   (lists the skippable nodes and their types)
"""
from __future__ import annotations

from pathlib import Path
import sys

from seat_cameras import BinXml, decode, encode

NODES = {
    "movies": (("StateVideoPreStart", "sting_video"), ("StateVideoPreStart", "amd_sting_video")),
    "legal": (("ScreenInitialLegal", "screen_initial_legal"),),
    "cutscene": (("StateGarageZone", "press_start_cutscene"),),
}
WAYPOINT, DELAY, ZERO = "StateWayPoint", "timeDelay", "0.0"


def find(document: BinXml, tag: str, ident: str) -> int:
    """The one element with this type and id; either type is accepted (already skipped or not)."""
    found = [i for i in range(len(document.elements))
             if document.name(i) in (tag, WAYPOINT) and document.attributes_of(i).get("id") == ident]
    if len(found) != 1:
        raise ValueError(f"Expected one {ident} state")
    return found[0]


def _waypoint(document: BinXml, element: int) -> None:
    if document.name(element) == WAYPOINT:
        return
    name, value, count, first, children, child_first = document.elements[element]
    if count < 2 or children or document.string(document.attributes[first][0]) != "id":
        raise ValueError(f"Unexpected {document.name(element)} state")
    for other, (_, _, other_count, other_first, _, _) in enumerate(document.elements):
        if other != element and other_count and set(range(first, first + count)) & set(range(other_first, other_first + other_count)):
            raise ValueError("Shared attribute records")
    strings = document.strings
    if WAYPOINT not in strings or DELAY not in strings or ZERO not in strings:
        raise ValueError("Unexpected states.bin string table")
    document.elements[element] = [strings.index(WAYPOINT), value, 2, first, 0, child_first]
    document.attributes[first + 1] = [strings.index(DELAY), strings.index(ZERO)]


def skip_startup(data: bytes, parts: tuple[str, ...]) -> bytes:
    """Turn the chosen parts ("movies", "legal", "cutscene") into StateWayPoint nodes; idempotent."""
    if not parts or any(part not in NODES for part in parts):
        raise ValueError("Unknown startup part")
    document = decode(data)
    if document.name(0) != "xml":
        raise ValueError("Not a states file")
    for part in parts:
        for tag, ident in NODES[part]:
            _waypoint(document, find(document, tag, ident))
    return encode(document)


def listing(data: bytes) -> list[tuple[str, str, str]]:
    document = decode(data)
    return [(part, ident, document.name(find(document, tag, ident))) for part, nodes in NODES.items() for tag, ident in nodes]


if __name__ == "__main__":
    for part, ident, tag in listing(Path(sys.argv[1]).read_bytes()):
        print(f"{part}\t{ident}\t{tag}")
