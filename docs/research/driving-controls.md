# Driving controls in DiRT 3

Research for #e0fba42e (2026-11-02), for the launcher's binding wizard and calibration
items (#9b2935b5, #8121970e, #a72b44ea). Target: Steam DiRT 3 Complete Edition.

## Where controls live

- **Device presets: `actionmap/*.xml` in the game folder, plain XML.** `actionMapPaths.xml`
  lists 49 maps, each read by the `ActionMap2` processor into the `UPDATE` map. Every
  map has `actionMapName`, the DirectInput `deviceName` it applies to, and a `priority`:
  `dinput_default.xml` (`di_device_default`), `wm_keyboard.xml` (keyboard, plus AZERTY),
  `xinput_pad.xml` and `xinput_wheel.xml`, and 44 device presets (Logitech G25/G27/
  Driving Force/MOMO/pads, Thrustmaster T500 RS/RGT/F430/…, Fanatec Porsche, SideWinder).
  Two stock files are not well-formed XML (`l3_topdrive_gt.xml`, `lg_g27_res.xml`), so
  the game's parser is lenient.
- **The player's own bindings and options: the profile save**, in Steam Cloud
  (`Steam\userdata\<id>\321040\savegame@profile0#…`, obfuscated names, up to ~270 KB).
  Not plain files; we don't parse or write them.

Format, the same as DiRT 2:

```xml
<ActionMap actionMapName="lg_g27" deviceName="Logitech G27 Racing Wheel USB" priority="3">
  <Action actionName="Accelerate">
    <Axis axisName="di_y_axis">
      <CalibratorSimple type="uniDirectionalNegative" deadZone="0.0" saturation="1.0" />
    </Axis>
  </Action>
  …
```

- 72 action names across the stock maps. Driving: `Accelerate`, `Brake`, `Handbrake`,
  `Clutch`, `Steer Left`, `Steer Right`, `Gear Up`, `Gear Down`, `Gear 1`–`Gear 6`,
  `Gear Reverse`, `Horn`, `Change View`, `Look Back/Left/Right/Up/Down`, `Pause`; plus
  menu, replay, spectator, YouTube and `SeparationInc/Dec`, `ViewPlaneInc/Dec`.
- Axis names: `di_x_axis`, `di_y_axis`, `di_z_axis`, `di_[xyz]_axis_rotation`,
  `di_slider_N`, `di_button_N`, `di_dpad_N_{up,down,left,right}`, keyboard
  `vk_code_0x..`, and `xinput_*` (sticks, triggers, buttons, d-pad).
- One calibrator, `CalibratorSimple`: `type` = `uniDirectionalPositive` /
  `uniDirectionalNegative` / `biDirectionalLower` / `biDirectionalUpper`, optional
  `deadZone` and `saturation` (map comment: defaults 0.2 / 0.8, threshold 0.5). No
  linearity in the maps.

## What the game's own menus offer

From the English strings (`tools/event-catalog lng`):

- Controls: CHOOSE PRESET (Defaults, Keyboard, Logitech G25/G27/Driving Force (Pro)/MOMO/
  RumblePad 2, Thrustmaster RGT FF Pro / Run'N'Drive, Microsoft SideWinder, Fanatec
  Porsche, Xbox 360 Controller, XInput wheel, Presets 1–3), per-action remapping
  (BUTTONS), REVERT TO PREVIOUS, and an "INVALID CONTROLLER SETUP" check for essential
  actions.
- ADVANCED WHEEL SETTINGS: steering dead zone, saturation and **linearity**; throttle
  dead zone and saturation; brake dead zone and saturation.
- VIBRATION strength.
- Transmission (assists): manual sequential, MANUAL H PATTERN, MANUAL H PATTERN + CLUTCH;
  FORCE MANUAL GEARS (online).
- Not in the game: wheel rotation (the wheel driver's job), clutch and handbrake axis
  dead zone/saturation.

## Direct launch does not load the profile

`system/flow.bin` (read with `tools/event-catalog xml`): the normal start goes
`press_start` → `press_start_handle_signin` → … → `profile_reset` → `profile_reset_p2` →
`create_protected_data_context` → `load_profile_dataset_to_ep` → `begin_load_profile` →
`wait_for_load_profile` → `check_loaded_profile` → …. A `skipfe` start (the benchmark /
direct launch) takes `skipfe_press_start_decision` → `skip` → `press_start_handle_signin`
→ DLC → `profile_reset` → `profile_reset_p2` → `clear_pchange_select` (`10`), **skipping
the profile load**, then on to the race.

So in a direct session the player's saved bindings, wheel settings, assists and
difficulty are not used; the game uses its defaults, i.e. the device preset matched by
`deviceName` (or `dinput_default` / the keyboard / XInput defaults). The difficulty this
PC reported in a direct session (level 0, runs 36–37) was most likely that default, not
iztok's setting. DiRT2VR had the same gap and redirected its direct-start
flow through the profile-loading states (`docs/driving-controls.md` there).

### Redirect test (in progress)

`run_benchmark.py --load-profile` applies a journaled `flow.bin` change that sends a
skipfe start through the normal profile load: node `32e` (`profile_reset_p2`) links
`next` to `C` (`create_protected_data_context`) instead of `10`. It is one attribute
(`tools/diagnostic_files.profile_load_redirect`; the string `C` already exists, so the
file keeps its size and every element). Run 44 (direct practice, difficulty hook at 5):
no prompt blocked the start, the race began normally, and the Steam Cloud saves were
unchanged. Whether the profile actually loaded is still open: the difficulty lookup
still answered 0 (which may be iztok's real setting), and Steam Cloud reads don't show
up in the in-process file trace (runs 45/46 opened the same 1263 files with and without
the change). iztok's profile is at difficulty level 1 of 6 (star 0) with all
assists off (Custom), so difficulty can't tell a loaded profile from the defaults.

Research traces (`run_benchmark.py --settings-trace`, research renderer only):
- the profile-setting lookup `0x5ba530` is only asked for `difficulty` in a direct
  session (0 in runs 47/48, with and without the redirect);
- `ISteamRemoteStorage` (version 012) `FileRead`/`FileExists`/`GetFileSize` hooks saw no
  Steam Cloud access at all in runs 49/50, with or without the redirect.

Positive control, run 51 (`--normal-start`, Enter at Press Start; focus test with
iztok's OK): the normal start checks and reads all of the profile's Steam Cloud files
through `ISteamRemoteStorage` (`FileExists`/`GetFileSize`/`FileRead`
`savegame@profile0#QKRHMYXE`, then 18 more, the largest ~279 KB). So the trace works, and
**the redirect at `32e` does not load the profile.**

Why: the benchmark start takes `skip_no_garage` at the first `skipfe_decision` (`n`), to
`32o`: `start_dlc_timer` → DLC enumerate/reset/load/restrictions → `wait_dlc_timer…`
(`32t`) → `shutters_slide_off` (`32u`) → `update_in_network_game` (`2Q`) → race. That
route has no profile reset or load at all; `32e` is on the `skip` branch it never takes.

Attempts with copied nodes on the `skip_no_garage` route (`--load-profile`,
`diagnostic_files.profile_load_redirect`; `seat_cameras.to_tree/from_tree` now insert
elements while keeping the game's exact layout, verified by a byte-identical tree round
trip of flow.bin, states.bin and all 63 cameras.xml):

| Run | Inserted after `32t`, then on to `32u` | Result |
|---|---|---|
| 52 | signin → create context → load dataset → [cleanup: begin → wait → check] | reads `QKRHMYXE`, then a black screen |
| 53 | the same inside `event_monitor` > `delay_invite_events`, as the normal start | the same |
| 54 | signin only | race starts; no profile read |
| 55 | signin → context → dataset | race starts; no profile read |
| 56 | … → [cleanup: begin] (no wait) | reads `QKRHMYXE`, then a black screen |

So `begin_load_profile` starts the save system's load, which reads the profile's first
file and then stalls; leaving the state doesn't help. In the normal start these states
run inside `press_start` (and its music/audio wrappers), which presumably keeps the save
operation going. Next: what `StateWaitForLoad`/the save system waits for (vtable route
below), or whether a `press_start`-like parent can run without showing its screen.

Why it stalls (2026-11-02, research renderer: `--stacks` samples every thread's stack,
`--count-calls` counts calls to chosen functions and keeps the first two call stacks):

- The main thread keeps rendering (run 58); no thread is stuck. The flow runs as a job:
  thread pool → `0x7660a0` → `0x9d7c60` → `0x9f1d10` → `0x4a1f60` → `0xd1cb10` → `0xb854a0`
  → `0xd04860` → `0xcd9d90` (the flow runner's phase machine, phase in `[runner+0x20]`)
  → `0xcd99a0` (active-state updates).
- In the stall the root keeps ticking (~1400 calls/s) but `0xcd99a0` and every state
  update stop (run 62), with `wait_for_load_profile`'s update (`0x54fef0`) called twice
  (run 59): the runner is stuck in a transition phase (waiting for a state to finish
  exiting or entering). Not caused by the research hooks (run 62 had no Steam hooks); key
  presses don't release it (run 60).
- `0x54fef0` decoded: it looks up the running save operation by type; operation status
  `0x3c` while the component at `0xfe45d0` reports a status other than `0x3c`
  (`0x5a7400`) returns `list_select`. My copies sent `list_select` out of the
  `cleanup_profile_operation` wrapper, whose exit waits for the open operation forever.
  The normal start sends it to `31l delay_system_events > 31i save_info_list`
  (`ScreenOverlaySaveInfoList`), which continues to the wait.

| Run | Variant | Result |
|---|---|---|
| 57 | `reset`: + `profile_reset` / `profile_reset_p2` before the load | same stall |
| 63 | `list`: + `d3x delay_system_events > d3l save_info_list, d3j generic_waypoint`, `list_select` → `d3l` | flow keeps running (no freeze); black screen, only the index file read |
| 64 | `list`, Enter at 20 s (focus test) | **all 19 profile files read**, 8 s after the index, right after Enter; then a later transition freezes again, race doesn't start |

So in a direct start the save system stops at an (invisible) save list after reading
the profile index and waits for a choice; the normal start never shows that list (its
next screen is the AUTOSAVE WARNING, run 51), so something there chooses automatically,
presumably what Press Start sets up (the `0xfe45d0` component's status). Next: find what
makes the normal start skip the list (the `0xfe45d0` status object at `+0x1c`, set up
around Press Start / `press_start_handle_signin`), or choose the save without input; then
the transition after the load.

Runs 65–83 (research hooks; all in the background unless noted):

- `press_start_user_index` (read by `StateHandleSignIn`'s enter `0x55eaf0`, written by
  `ScreenPressStart`'s Start handler `0x4f95b0`) is already 0 in a direct start; setting
  the input component's user (`+0x1b4`) as well changes nothing (runs 65, 76).
- The save list is part of the normal start too (run 72, reference: Press Start, then the
  second Enter chooses the save). Its confirm (`0x4e9340`) calls
  `SaveSystemUserInterface::0x5a7420(action, index)`: `(0, i)` loads save i, `(1, 0)`
  creates one, `(2, 0)` backs out. The load worker waits for that answer on the UI's
  event (`0x5b9780`). `--auto-save-choice` answers `(0, 0)` from
  `wait_for_load_profile`'s update: all 17 profile files are then read with no input
  (run 67).
- But the load then reports the save as corrupt: the UI is asked to show
  `lng_ss_save_corrupt` (`0x5a7490`, flag `+0x48`, which the wait returns as `corrupt`),
  and the operation never finishes (status stays `0x3c` = running). The save system's
  own log (sink `[[0xfe5a18]+0xc]`, research hook) shows the save mounted from
  `Steam\userdata\…\321040\local\savegame\profile0`, then error `0fdf:0056` =
  "Serialiser reports the save '%s' is corrupted" (`0xcc7b38`). The serialiser
  (`0xb2ef00`, vtable `0xf29278`) allocates fine, but a section decoder fails: **section
  `career manager sdu name`, error `0x37`** (run 83).

Runs 84–89 followed the failure down (register capture at chosen addresses): the sixth
section decoded is the career manager's (`0x5f3a60`, vtable `0xeb6840`); its main career
(`+0x78`) decodes fine, `+0x80` is absent, and the object at `+0x94` fails in its decoder
`0x6f4000` (interface vtable `0xec5898`; the `track_model` table is present), whose first
entry (`0x6f1530`) gets `0x37` from the reader `0x9d5320` = **read past the end of the
chunk**, on the entry's first field. So on the race route the career manager holds a
different object in that slot than the one that wrote the data, and misreads it.

So the profile files are fine (the normal start loads them) but the career manager
cannot take its data in a direct start, presumably because it is only set up with the
front end (garage / events database), which the race route never loads. Skipping that
section is not safe: the career would not be loaded, and a later profile save would
write a profile without it. Making the race route build the career manager as the front
end does is deep and risky work; the practical options are (a) keep DiRT 3's defaults in
direct launch and let the launcher apply what matters itself (difficulty already; controls
through per-session action maps, step 2 below), or (b) a direct launch through the front
end (normal start, profile load answered automatically, then into the race), a much
larger change. Every run restored its
files; the saves were unchanged throughout.

The earlier plan: a profile load on the `skip_no_garage` route. The existing load nodes (`C` →
`D` → `E` → `F` → `G`) are shared with the normal start and continue into the front end
(garage, main menu), which this route never loads, so they can't simply be linked to.
The fix is DiRT2VR's: copies of those nodes with new ids (same `state` names), inserted
after `32t`/`32u` and returning to `2Q`. In the binary flow file that means inserting
elements and renumbering the element table's child ranges, a bigger edit than changing a
value.

## Recommendation

1. **Load the profile in direct launch** (so the game's own controls menu works for
   direct sessions too): redirect `profile_reset_p2`'s `next` from `10` to the
   `create_protected_data_context` chain in a journaled `flow.bin` copy, as DiRT2VR did.
   Needs a test that a profile loads without a "press start" screen or save prompts.
2. **Launcher bindings, data first**: because a direct session uses the plain-XML device
   presets, a journaled per-session action map for the player's device (generated from
   the launcher's bindings, all 72 actions or only the driving ones) may be enough, without
   a native hook. To verify: whether a generated map overrides the stock preset for the
   same `deviceName` (priority semantics), and whether a loaded profile's own bindings
   then win over it. If data alone can't override a loaded profile, fall back to
   DiRT2VR's approach: a native hook on the ActionMap parser that substitutes only the
   configured actions.
3. Calibration the game lacks (clutch/handbrake dead zone and saturation) fits the same
   generated `CalibratorSimple` attributes; linearity only exists in the profile's wheel
   settings.

Open: the two checks in step 2 need key presses in a short focus test.
