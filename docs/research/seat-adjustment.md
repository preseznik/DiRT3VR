# Seat adjustment through the car camera files

Research for #2fdaf181 (2026-11-01), game side. Target: Steam DiRT 3 Complete Edition,
`cars/models/<car>/cameras.xml` of the 63 installed cars.

## Result

The cockpit view is the `View` with `ident="head-cam"` (`type="Head"`). Its
`<Parameter name="position" type="vector3" x=… y=… z=…/>` is the car-local eye point in
metres, written with four decimals (frx: `0.3460`, `1.0680`, `-0.3170`). Changing it moves
the cockpit view. Desktop runs with the frx (direct practice, screenshots at 45 s,
`artifacts/diagnostics/20261101-23*`):

| Run | Offset (x, y, z) m | View |
|---|---|---|
| 40 | 0, 0, 0 | stock cockpit: wheel, hands, dashboard, windscreen |
| 41 | 0, +0.3, 0 | eye at the roof line, looking over the dashboard |
| 42 | 0, 0, +0.3 | eye at the wheel, much closer to the windscreen |
| 43 | +0.2, 0, 0 | eye by the left door pillar, wheel to the right |

So **+x = left, +y = up, +z = forward** (the frx is left-hand drive: x = +0.346 is the
driver's side).

## File format and edit rule

The files are Ego binary XML (`1a 22 52 72`): total length, then a string container
(`17…`, its length) with the strings (`1d…`, UTF-8, NUL-terminated, the payload padded
with 0–15 NULs to a multiple of 16) and one 4-byte offset per string (`1e…`), then the
element table (`1b…`, 24 bytes each: name, value, attribute count, first attribute, child
count, first child) and the attribute table (`1c…`, 8 bytes each: name, value). All
integers little-endian.

`tools/seat_cameras.py` decodes and re-encodes all 63 stock files byte for byte. A seat
offset changes attribute values only: strings are shared, so a new value is appended to
the string table (or an existing equal string reused) and only the position's `x`/`y`/`z`
attributes point at it. Elements, other attributes and existing strings are unchanged.
`offset_seat(data, sideways, up, forward)` accepts ±0.5 m per axis; zero returns the
original bytes. `tests/test_seat_cameras.py` checks the round trip of every installed
car, that only the position values change, and that `tools/research/inspect_cameras.py`
(an independent decoder) reads the result.

For the launcher (#2fdaf181, Dash): per car, read the stock file (pin its SHA-256), apply
the player's offsets with this rule, write the result into the session's journaled
changes, and restore the stock file afterwards, as with the other temporary game-folder
changes. Only cars with a non-zero offset need a change.

## Also found

`start_in_cockpit` (research): hiding every visible view listed before `head-cam` makes a
race start in the cockpit view; DiRT 3 starts with the first visible view. That is how
runs 40–43 got a cockpit view without key presses, and could become a VR "start in
cockpit" option.

Not yet checked: in VR (the headset adds its own tracked offset on top), and whether the
`head-cam_reverse` view (look back) should follow the seat.

## The player's car at runtime (#88b7f778)

For per-car seats in the in-game menu the renderer needs the car being driven. DiRT 3 keeps
the race's cars as 16-byte names in a static table at `0xf9fc08` (entry *i* at
`0xf9fc08 + 16*i`), counted by `[[0xf9fbfc] + 0xb234]`; the race load's `Vehicle %2d: %s`
log lines (`0x53d33b`) print them. Each name is `<car>_<livery> <tag>`, NUL-terminated, so
the car code is the text before `_`.

Run `20261103-084448` (research proxy, `run_benchmark.py --direct-practice --car frx
--opponents 2 --vehicles 30,60,100`, no input): at 30, 60 and 100 s the count was 3 and the
entries `frx_00 sn`, `stx_00 sn`, `mex_00 sn`, i.e. entry 0 is the player's car in a direct
start (whose first car is always the player's). The race load's log lines are not reached in
a direct start (run `20261103-084140`, `--game-log`), so the table, not the log, is the
source. Not yet checked: a race started from the menus (whether the player is still entry 0,
e.g. in a career event with the player on a later grid slot) and the table between races.

## A live seat on the monitor (#88b7f778)

The desktop seat menu can move the cockpit view without reloading `cameras.xml`: the
selected head camera keeps its own copy of the file's values, and the view follows them
every frame. The Head loader (`0xb07cf0`, class vtable `0xf28530`) stores the eye point at
`+0x19d0` (x left, y up, z forward, metres), the base FOV at `+0x19e4` (degrees), tilt at
`+0x1a24`. The selected camera ("owner") is found as the renderer's camera classifier does:
`[[[[base+0xbe7404]+8]+0x11b4]+0x24]` is the camera manager, its entry `[manager+0x140]`
(entries of 0x280 bytes) the owner; it must have the Head vtable and the name `head-cam` at
`+0xa1`.

Run `20261103-113446` (research proxy, direct practice frx, `--seat-test 0,0,0` for a
cockpit start, `--live-seat 50:y=0.3,80:fov=20`, screenshots at 45/55/75/85 s, no input):
at 50 s the eye height went 1.068 -> 1.368 and the 55 s screenshot looks over the dashboard
from roof height (as the file edit in run 41 did); it stayed there at 75 s; at 80 s the FOV
went 50 -> 70 and the 85 s screenshot shows the wider view. So a menu writes the owner's
`+0x19d0..+0x19d8` and `+0x19e4` (the file's value plus the seat offset; keep the file's
values to apply only the difference, as the VR seat channel does with `applied`). The
research switch: `DIRT3VR_RESEARCH_LIVE_SEAT="s:y=M,s:fov=D"` (`run_benchmark.py --live-seat`).
Not yet checked: a car change or restart (whether the owner is rebuilt from the file), the
reverse view, and replays.

