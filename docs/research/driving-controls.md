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
the change). Next: iztok's real difficulty level, or a research hook on
`StateBeginLoad` (factory `0x523f60`, vtable `0xe9cb28`; overridden slots 1, 2, 3, 6, 7
against the base vtable `0xe90028`) to log that the load runs.

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
