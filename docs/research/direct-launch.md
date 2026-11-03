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

**Saving after a return (#f2d0f352).** A direct start never loads the player's profile
(docs/research/driving-controls.md), so the menus after a return run on DiRT 3's default
profile. Every profile save in `flow.bin` (pause-menu options, front-end options, unlocks,
event results) passes `auto_save_check`, a `StateDecisionBool` on the data flag
`auto_save` (store `0x10da300`); the profile setting `auto_save` defaults to 0
(`0x59f3cc`). Run 96 (`--direct-return --data-watch`, research renderer reading the flags
through the game's own lookup `0xd04300`): `auto_save=0`, `hasEverSaved=0` in the race and
in the main menu after the return; the Steam Cloud saves were unchanged (runs 95–96). So
the menus after a return should not save over the player's profile. Positive control, run
`20261103-143112` (`--normal-start --data-watch`, Enter at 40 s and 46 s as Press Start and
its prompt, focus test with iztok's OK): `auto_save=0` and `hasEverSaved=0` until the profile
loads, then `auto_save=1` (59 s) and both 1 at 70 and 85 s; protected files unchanged. So the
probe reads the right flags, and the menus after a direct-practice return (both 0, run 96)
cannot autosave over the player's profile (#f2d0f352).

Pause is blocked deliberately: the race's next-state check (`0x51f3f0`) returns "pause"
when a pause is requested, except while the benchmark flag is set (`0x51f456`). Direct
practice now makes that `je` unconditional too (byte-checked with the relocated flag
address). A direct session then has DiRT 3's full Single Race pause menu: CONTINUE /
RESTART / RESTART VIA SERVICE AREA / RESET VEHICLE / REPLAY / OPTIONS / RETIRE (run 22,
iztok pressing Esc; run 23 with the real renderer). Retire asks "RETIRE FROM EVENT?" and,
confirmed, closes the game cleanly without a result (run 23). In run 22 a pause choice
led through the Aspen event intro to the Single Race pre-race screen (RACE / ENTRANTS /
DIFFICULTY AND ASSISTS / VEHICLE SETUP); RACE started a new race with the player in
control.

Restart (run 26) asks "RESTART RACE? Really restart the race? Your progress in this race
will be lost. RESTART / CANCEL"; confirmed, the car is back on the start line with the
timer at 0:00, no intro, and the player keeps control.

Checks that need keyboard input use `run_benchmark.py --focus-keys`, only with the
user's permission: it brings DiRT 3 to the front and presses real keys, refusing to send
anything unless the game is the foreground window. Keys sent in the first ~40 s skip the
pre-race camera instead.

Run 19 posted Esc to the game window at 40 s: no pause menu appeared and the timer kept
running. That doesn't settle whether the benchmark can pause: DiRT 3 reads the keyboard
directly, so window messages likely never reach it. The pause menu still has to be checked
with real input.

## Opponents and disciplines

`<car name number>`: `number` is how many copies of that car race (the game's own
`example_benchmark.xml`: "up to 10 tracks, 8 cars per track", `<car name="aqb"
number="8"/>`). **The first `<car>` is the player's car**; every other car is AI.
Copies past 8 in total are cut. Runs 24–28 (Aspen): 4 entries with `number="1"` give a
4-car field; with direct practice the player waits at the start while the AI cars race
(run 28: `mex` first → the player in the Mitsubishi). `<car frx number="4"/>` alone gives
the player's `frx` plus 3 AI copies (run 34). `run_benchmark.py --route`, `--car`,
`--opponents N` and `--player-copies K` build such files from the catalog.

All four catalog disciplines start and race (AI driving): rallycross (Aspen), rally
(Finland route 0, run 29), trailblazer (Finland trail route 0, run 30) and Land Rush
(Aspen route 6, run 31). Stages take opponents too (runs 32–33), but run with staggered
starts, so the player races the clock. On a stage, a car that never leaves the start is
ended after about 45 s of race time, with a result written, with or without opponents
(run 35); on circuits a waiting car is left alone (runs 10, 17).

## AI difficulty

The race setup (`0x69ca05`) asks `0x5ba5c0` for the profile's `"difficulty"` setting
(`"ss_difficulty"` in split screen; −1 when absent), the star level the player picks under
Single Race → Difficulty and Assists. `0x5c9560` turns it into the AI pace ratio from the
database's `difficulty` table (`star_level` 0–5 per season; `ai_ratio` about 0 at star 0 up
to 1.0 at star 5; flashbacks 5 → 0; season falls back to
`game_settings.difficulty_default_season` = 5). A benchmark start therefore uses the
player's saved difficulty; on this PC it is 0, the easiest.

`DIRT3VR_DIRECT_DIFFICULTY=N` (0–5) hooks `0x5ba5c0` (byte-checked, MinHook) and answers N
while the benchmark flag is set; otherwise the profile's value passes through unchanged.
Runs 36–37 (Aspen, AI driving `frx` with 3 opponents): at level 5 the game asked three
times and got 5 instead of the profile's 0, and the race went differently (the AI-driven
player car 2nd of 4 at 40 s on the clock, against 4th of 4 at level 0, since the AI on the
player's car follows the same level). How much faster the opponents are against a human
still needs a drive.

## Still open

- Confirm that the main menu works after a return (starting another event, Exit).
- Opponent pace per difficulty level against a human driver.
- Opponents (more `<car>` entries, as DiRT2VR's race grid), and which disciplines and
  routes start correctly. The event catalog is #5b02d01b.
- Not yet tried in VR.
