# Seat channel: the in-game seat menu's launcher ↔ renderer contract

For #bd820b32 (in-game seat menu for VR and the monitor). Launcher side: #c137c681
(`launcher/SeatChannel.vb`). Renderer side: #35826889 (to port DiRT2VR's
`src/seat_adjustment.cpp`). Runtime camera research: #88b7f778.

The layout follows DiRT2VR's seat channel version 2 (DiRT2VR `launcher/SeatPositions.vb`
`SeatChannel` ↔ `src/seat_adjustment.cpp`) so the renderer port stays close to that code.
DiRT3VR changes: its own name, version 1, a car table stride of 96 bytes with an **applied**
offset per car, and at most 64 cars.

## Ownership

- The **launcher** creates the mapping, owns every disk write and polls controllers (as it already
  does for the recenter button). It writes the car table, the keys, the held controller mask and the
  hint text, and saves each commit into the launcher settings (Controls → Seat).
- The **renderer** owns the menu: open / close, live preview, keyboard keys (`GetAsyncKeyState`),
  blocking the menu's inputs from the game, and publishing state and commits.
- No other process writes the mapping. Every value is validated on read; a bad header disables the
  menu for the session.

## Opening

`Local\DiRT3VR.Seat.<32 hex>` (`MemoryMappedFile.CreateNew`, 16384 bytes). Its name reaches the game
in `DIRT3VR_SEAT_CHANNEL`. The mapping lives as long as the launcher's session object; the renderer
opens it with `OpenFileMappingW(FILE_MAP_ALL_ACCESS)`.

## Layout (little-endian; offsets in bytes)

| Offset | Type | Writer | Meaning |
|---|---|---|---|
| 0 | u32 | launcher | magic `0x54414553` ("SEAT") |
| 4 | u32 | launcher | version, 1 |
| 8 | u32 | launcher | size, 16384 |
| 12 | u32 | launcher | heartbeat: `Environment.TickCount64` low 32 bits, every poll |
| 16 | u32 | launcher | input sequence (seqlock: odd while 20–79 are written) |
| 20 | u32 | launcher | held controller mask (bits below) |
| 36 | u32 | launcher | source of the held device: 0 none, 1 XInput, 2 DirectInput |
| 40 | u32 | launcher | XInput slot 0–3 |
| 44 | GUID | launcher | DirectInput instance GUID |
| 60 | u32[4] | launcher | buttons the menu uses on that device (XInput: `wButtons` mask in [0]; DirectInput: bit n of the 128 = button n) — the renderer blocks them from the game while the menu is open |
| 76 | u32 | launcher | 1 when the menu uses that device's hat (POV) |
| 80 | u32 | renderer | menu open (1) or closed (0) |
| 84 | u32 | renderer | 0 ready, 1 car not identified, 2 input filtering unavailable |
| 88 | i32 | renderer | active car index, −1 none |
| 92 | u32 | renderer | state sequence (seqlock, odd while 80–127 are written) |
| 96 | f32[3] | renderer | current (preview) position |
| 108 | f32[3] | renderer | last committed position |
| 120 | i32 | renderer | index of the last commit: car index, or −2 for all cars |
| 124 | u32 | renderer | commit generation (incremented per commit) |
| 128 | (u32 key, u32 modifiers) × 7 | launcher | keys: [0] open / save & close, [1] left, [2] right, [3] up, [4] down, [5] back, [6] forward; key 0 = unbound; modifiers 1 Ctrl, 2 Alt, 4 Shift |
| 184 | u32 | launcher | car count (≤ 64) |
| 188 | u32 | launcher | 1 when one position is used for every car |
| 192 | f32[3] | both | position for every car |
| 204 | f32[3] | launcher | the every-car position written into the camera files this session |
| 216 | u32 | launcher | 1 = monitor session (Launch), 0 = VR session |
| 220 | i32 | launcher | monitor: the cockpit FOV offset the session's camera files hold (whole degrees, −20…+30 from each car's own) |
| 224 | i32 | renderer | monitor: the current FOV offset (inside the state seqlock) |
| 228 | i32 | renderer | monitor: the committed FOV offset (written with each commit) |
| 256 | car × 96 | launcher (position also renderer on commit) | car table |
| 10500 | UTF-16, 400 chars | launcher | hint text under the panel (the controller buttons in use) |

Car entry (96 bytes): code (UTF-8, 16 bytes, NUL-padded) at +0, display name (UTF-8, 48 bytes) at +16,
position f32[3] at +64, **applied** f32[3] at +76 (what the session's camera file already holds), 8
spare bytes.

The launcher writes the car table, 188 and 192–215 **once, when it creates the mapping** at session
start. After that only the renderer writes a position (the car's +64, or 192, on commit); the launcher
only reads it back. The applied values never change during a session.

Positions are metres in DiRT2VR's convention: **x right, y up, z back**, each within ±0.5. The
launcher's seat offsets are centimetres with +left / up / forward, so `x = −Sideways/100`,
`y = Up/100`, `z = −Forward/100`. The launcher saves commits rounded to whole centimetres.

**The renderer applies `position − applied` live** (VR: added to `frame.RelativeEyePose()` before
`cameras.Apply`, as DiRT2VR's `SeatEyePose` (Lens, 2026-11-02: EyeCamera uses 1 unit = 1 m on the car
camera's right / up / back axes, so the convention matches); on the monitor: per the camera research, #88b7f778). The camera file edit stays the
session's starting point, so a position is never applied twice and a session without the menu
behaves as before.

Held mask bits (DiRT2VR's): 0 left, 1 right, 2 up, 3 down, 4 back, 5 forward, 6 sideways modifier
(while held, left/right move sideways instead of back/forward), 7–10 navigation up / down / left /
right (D-pad or hat), 11 save, 12 cancel, 13 open. Monitor sessions only: 14 FOV wider (Xbox RB),
15 FOV narrower (Xbox LB); the launcher publishes them and lists those buttons at 60 only there.

## Monitor sessions (#c137c681 launcher, #35826889 renderer)

With **Seat menu on the monitor too (Launch)** and a menu key or button, Launch installs DiRT3VR's renderer
for the session (recovery journal schema 10 without direct practice) and passes `DIRT3VR_SEAT_CHANNEL`
and `DIRT3VR_LOG_DIR`. The launcher writes 216 = 1 and 220 = the FOV offset the session's camera files
hold (0 when the cockpit field of view on the monitor is DiRT 3's own); 224 and 228 start at that value.
The renderer draws the panel over the game image and writes the selected head camera's eye point
(+0x19d0) and FOV (+0x19e4) as the file's values plus (current − applied), following Wrench's research
(docs/research/seat-adjustment.md). Keys: PgUp wider, PgDn narrower, 1° per press, repeating while
held. A zero at 216 is a VR session, so the channel stays version 1.

## Commits

On save the renderer writes 108–127 inside its seqlock and also the position into the car's table
entry (or 192 for every car). In a monitor session it also writes the committed FOV offset at 228 with
every commit, and when no car is identified the commit index is −3: the field of view alone. The launcher reads a commit when the sequence at 92 is even and
unchanged across the read and the generation at 124 is new; it validates the position, rounds it to
centimetres, converts it to a seat offset and saves the launcher settings. A monitor commit's FOV offset
(−20…+30) becomes Controls → Seat → Cockpit field of view (monitor); one out of range is ignored. The applied values do not
change during the session.
