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
