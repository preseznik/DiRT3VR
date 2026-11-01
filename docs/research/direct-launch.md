# Direct launch into an event with the player in control

Target: Steam DiRT 3 Complete Edition `dirt3_game.exe`, SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`, image base `0x400000`.
Research for #a1d31909 (direct Practice / Race launch, #8e0203c0), 2026-10-31 to 2026-11-01.

## Result

DiRT 3 can start straight into a chosen track with `-benchmark <file>`, and with one
in-memory byte change **the player drives**. iztok confirmed this in run 8
(2026-11-01 15:38, Aspen route 0, car `frx`): the car responded to his input.

The renderer applies the change with `DIRT3VR_DIRECT_PRACTICE=1` (`src/direct_practice.*`,
native test `direct_practice_site`). `tools/run_benchmark.py --direct-practice` uses it.

## How the benchmark start works

- `-benchmark <file>` (and the fixed AMD showcase modes `-demo`, `-amd_g`, `-amd_v`, which
  read `system/amd*.xml`) is parsed by `0x6aa900` into a static config object at
  `0xf9ce70`. Attributes: `skipreplays`, `outputfilename`, `hardwaresettings`; per
  `<track country name route>` up to 8 `<car name number>`. There is no control or laps
  attribute.
- The parser sets `[0xf9ce70+0x80]` = `0xf9cef0` ("benchmark active") and
  `[+0x81]` = `0xf9cef1` (1 from the command-line parsers, 0 from the in-game graphics
  benchmark at `0x620bc4`).
- With both set, `0x55982c` switches the career to `skipfe` ("skip front end", career
  ID 5) through `0x429720`. `0x604931` then takes country, track and route from the
  benchmark config instead of `track_model.skipfe_track`. `0x61f22d` adds a benchmark
  session object (`0x61d5d0`, vtable `0xeb7540`); the race crashes without it (run 4).
- The hardware-settings loader reads `0xfd8a50` to use the benchmark's own settings file
  (see `observer-followup.md`).

## Why the AI drove, and the fix

DiRT 3's controller update at `0xb29f80` is the exact counterpart of DiRT 2's
`0x72a580` (DiRT2VR `src/trace.cpp`):

```
b29f9b  cmp  byte ptr [ecx+0x1c],1
b29fa5  jne  ...
b29fab  cmp  byte ptr [0xf9cef0],0      ; benchmark active
b29fb2  mov  eax,[ecx+0xc]
b29fb8  je   b29fc0                     ; 74 06  ->  EB 06
b29fba  mov  byte ptr [ecx+0x2c],1      ; force AI
b29fbe  jmp  b29fc4
b29fc0  mov  byte ptr [ecx+0x2c],0
```

While the benchmark flag is set, every update forces the controller to AI. Making the
`je` unconditional keeps the human/AI choice the game made when it created the
participants. Outside a benchmark start the game takes that branch anyway.

## What was ruled out on the way

All desktop `-benchmark` runs with `--background-play`, no input sent, every file
journaled and restored (`artifacts/diagnostics/20261101-*`):

| Run | Change | Result |
|---|---|---|
| 2 | `[obj+0x10b8]` list kept at mode 2 (`0x6970bf`) | AI drove |
| 3 | `cutscene/spectator_startsequence.xml` given the normal race's `Controller / Switch type="human"` | AI drove |
| 4 | skip the benchmark session object (`0x61f234`) | crash after 20 s |
| 5–6 | file trace | all `cutscene/*.xml` load at startup as a library |
| 7 | start-sequence `Switch type="ai"` writes human (`0x5fc1ba`) | AI drove |
| 8 | **`0xb29fb8` je → jmp** | **player drives (iztok)**; no result written |

Useful facts from those runs: the start-sequence `Controller` event (parser `0x5e6760`,
apply `0x5fc070`) writes the player's control mode to `[component(0xff67d8)+0x16c]`
(0 ai, 1 human); a normal race's `startsequence.xml` switches to human at the lights.

## Event catalog (#5b02d01b)

`tools/data/event-catalog.json` lists what the picker can offer, extracted read-only from
the installed game by `tools/event-catalog` (EgoEngineLibrary with Ego-Engine-Modding's
`schemaDirt3.xml`, both MIT; run
`dotnet run --project tools/event-catalog -- catalog <game> <schemaDirt3.xml> <out.json>`):

- `disciplines`: the four the game marks `valid_for_benchmark` (rally, rallycross,
  trailblazer, landrush), with display name, `trackType` (`circuit` /
  `point_to_point`), `staggeredStart` and the Single Race lap choices (1–5 for circuits,
  1 for point to point). Gymkhana, head-to-head and joyride are not benchmark-valid.
- `routes` (80): database id, discipline, `country`/`track`/`route` (exactly the benchmark
  XML's `<track country name route>`), route, track and location names, type and length
  in metres (0 for circuits). Only routes whose folder is installed and not marked
  `not_single_race`.
- `cars` (55): `code` (the benchmark XML's `<car name>`), full name, discipline, class id
  and class name. Only installed cars, no easter eggs or dummy swaps.

Names are English (`language_eng.lng`, `db_<name_string_id>`). The catalog records the
database and language hashes. `tests/test_event_catalog.py` checks consistency and, when
DiRT 3 is installed, that every route and car exists. Only Aspen route 0 with `frx` has
been driven so far; cross-discipline car choices are untested.

## Laps

The benchmark XML has no laps attribute, but the config has a count at `+0xef4`
(`0xf9dd64` at the preferred base). The parser leaves it 0. The race setup at `0x608fbe`
uses `max(1, count)` as the race's lap count while the benchmark is active. The AMD
gymkhana showcase (`-amd_g`, `0x59c1ce`) is the only code that sets it, to 5.

`DIRT3VR_DIRECT_LAPS=N` (1–20, with `DIRT3VR_DIRECT_PRACTICE=1`) makes the renderer write N
into the count on Present while the benchmark flag is set, so it is in place before the
race loads. Present is only hooked with `DIRT3VR_DIAGNOSTICS=1`, which VR sessions always
set; a desktop direct launch must set it too. The launcher offers 1–5 for circuits
(rallycross, Land Rush), as the game's Single Race does, and leaves it unset for
point-to-point events.

Evidence: Aspen route 0, AI driving, the same write from the research proxy
(`run_benchmark.py --research-laps N`). Race time is samples ÷ average fps from the
result file:

| Run | Laps | Average fps | Race time |
|---|---|---|---|
| 9 | 1 (default) | 714 | 93 s |
| 13 | 2 | 673 | 155 s |
| 11 | 3 | 19 | 212 s |
| 12 | 3 (player's own fullscreen settings) | 122 | 215 s |

That is about 32 s of start sequence plus about 61 s per lap.

## The player's hardware settings

`hardwaresettings="hardware_settings_config.xml"` points the benchmark at the player's own
file. Run 12 (`--player-hardware-settings`) started at the player's settings (1920×1080
fullscreen, vsync, 4× MSAA, 120 Hz in the result), completed normally, and left the file
byte-identical. The launcher's direct practice uses it, so VR's temporary graphics
changes and quality choices apply as in normal play.

## How a session ends

Run 18 (AI, 1 lap, `--screenshots`): after the finish line the game shows a letterboxed
finish camera with the time animating in for about 8 s. It then writes the benchmark
result (the track counter `+0xefc` reaches `+0xef8`, `0x6a8720`) and quits to the desktop
by itself, with exit code 0. There is no results screen, Restart or return to the menus;
the launcher's cleanup runs after the exit.

The quit is code, not the flow files: after the last track `0x6a87d8` writes the result
and, for a command-line benchmark (config `+0x81`), `0x6a87ec` calls `ExitProcess(0)`.
`flow.bin` itself would take a `skipfe` race back to the main menu
(`last_raced_career_decision` → `skipfe` → `exit_zone` → `main_cutscene`).

`DIRT3VR_DIRECT_RETURN=1` makes the renderer jump past that call (the `je` at RVA
`0x2a87e4`, byte-checked with the relocated import slot) and, on the first Present after
the result is written (completed tracks `+0xefc` reach requested `+0xef8`), switches the
benchmark flags `+0x80` and `+0x81` off, so the menus and any race started from them run
as a normal session. Run 20 (the jump alone) and run 21 (`--direct-return`, real
renderer, AI driving) went from the finish camera to DiRT 3's main menu and stayed there.
That the menus then behave normally (starting another event, Exit) still needs real input.

Pause is blocked deliberately: the race's next-state check (`0x51f3f0`) returns "pause"
when a pause is requested, except while the benchmark flag is set (`0x51f456`).

Run 19 posted Esc to the game window at 40 s: no pause menu appeared and the timer kept
running. That doesn't settle whether the benchmark can pause: DiRT 3 reads the keyboard
directly, so window messages likely never reach it. The pause menu still has to be checked
with real input.

## Still open

- Verify with real input that the menus work after a return, and allow the pause menu
  (`0x51f456`) and check what its entries do in a benchmark start. A Restart at the finish
  would need more (DiRT2VR patched `flow.bin`/`states.bin` for a finish menu).
- Opponents (more `<car>` entries, as DiRT2VR's race grid), and which disciplines and
  routes start correctly. The event catalog is #5b02d01b.
- Not yet tried in VR.
