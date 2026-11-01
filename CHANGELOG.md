# Changelog

## Unreleased — clearer profile saving and a sturdier profile test

- Add quick VR presets above the quality controls (adapted from GRID2VR, #8df24f27): **Potato / Low-End** sets VR quality to Ultra Low and VR render scale to 70%; **VR Balanced** keeps High's look with the crowd one step lower (VR renders every scene twice) and leaves the render scale alone, and the VR preset picker names it. Desktop quality is left alone. **Undo preset** brings back the desktop and VR quality and render scale from before the last preset (quick or picked) until you change an option or the render scale yourself. Restore graphics defaults resets both quality sets. 10 new launcher checks pass under Wine; checked on screen at 1200 px.
- Add **Graphics → Quality** with separate **Desktop** and **VR** columns (adapted from GRID2VR's graphics catalog; values from ClaudeCode's research of DiRT 3's own options file, #f89609d3; separate VR quality as iztok decided): each column has a preset picker with DiRT 3's Ultra Low, Low, Medium (game default), High and Ultra presets, which shows Custom when the options match none of them, and one control per option: night lighting, shadows, particles, mirrors, crowd, cloth, ambient occlusion, ground cover, vehicle detail, trees, objects, car reflections, water, post-processing and skidmarks. Desktop options start on **Use game setting** and chosen ones are written into DiRT 3's graphics file at every launch (desktop and before VR sessions), with exactly the attributes the game's own options write (Crowd Off only switches the crowd off, as in the game); display, damage and other settings are untouched, and the file is backed up first. VR options start on **Same as desktop**; chosen ones become part of the VR session's temporary graphics changes, are recorded in its recovery journal (so recovery still checks the exact approved transform) and are undone when VR ends. The catalog (`tools/data/graphics-options.json`) is embedded in the launcher. Settings profiles now export schema 4 with both sets; schema 1–3 profiles load with every option on Use game setting / Same as desktop. Restore graphics defaults and Restore launcher defaults reset both. Launcher checks pass under Wine, including transforms of the real 31 Oct graphics file and VR quality through preparation, recovery and the keep-game-changes merge.
- Add **Keep the horizon level when recentering** under Controls → VR and seat controls (adapted from GRID2VR; on by default, which is today's behaviour). Turned off, the recenter key or controller button also resets pitch and roll, so a headset that sits tilted can be levelled; the automatic centering at the start of VR always keeps the horizon level. The launcher passes DIRT3VR_RECENTER_TILT to the renderer, which logs "tilt=1" with such requests and "including tilt" when it recenters. The choice is a launcher setting; settings profiles don't carry it yet and keep this PC's choice when loaded. 9 new launcher checks pass under Wine; the native math test (now with a tilted view) passes with g++, and xr_runtime_test (with a new tilt scenario) builds with 64-bit MinGW and passes under Wine. Not yet tried in the headset.
- Show what the last VR session ran with under Graphics → VR rendering (adapted from DiRT2VR's refresh-rate display and GRID2VR's measured scene): the headset refresh rate and whether DiRT 3 rendered the scene size DiRT3VR asked for, or which size it used instead. The renderer now enables XR_FB_display_refresh_rate when the runtime offers it and logs the rate after creating the session; this is diagnostics only and never affects VR. The launcher reads the newest session log. 10 new launcher checks pass under Wine; the renderer change compiles with MinGW and still needs an MSVC build and a headset run.
- Clean up old diagnostic data automatically (adapted from DiRT2VR's cache cleanup). At most once a day, in the background after the launcher opens, DiRT3VR removes VR session log folders (`logs\session-…`, including F8 captures with their raw RGBA images) and the records of finished recoveries (`restored-*.json`, the backups only they name, and old `game-written-*.xml` copies) once they are older than 30 days, always keeping the newest 10 of each. It never runs while recovery is pending or DiRT 3 is running, never follows links, and leaves anything it doesn't recognize, anything recent or in use, unreadable records and backups that a kept record still names. `logs\cleanup.json` says what was removed. Downloaded installers were already replaced by each new download. 15 new launcher checks pass under Wine.
- Add the event catalog for direct launch: `tools/data/event-catalog.json` lists the four disciplines DiRT 3 marks valid for its benchmark start (rally, rallycross, trailblazer, Land Rush) with their lap choices, the 80 installed routes and the 55 installed cars, with English names and exactly the country/track/route and car codes the benchmark file needs. Extracted read-only from the game's own database by `tools/event-catalog` (EgoEngineLibrary with the MIT-licensed DiRT 3 schema); format in `docs/research/direct-launch.md`. 3 new Python tests, including a check that every entry is installed.
- Start DiRT 3 straight into a chosen track with the player driving (groundwork for direct Practice / Race launch). DiRT 3's only custom direct start, `-benchmark <file>`, made the AI drive: its controller update (0xb29f80, the same code as DiRT 2's) forces the car to AI on every update while the benchmark flag is set. With `DIRT3VR_DIRECT_PRACTICE=1` the renderer makes that one branch unconditional in memory after checking the surrounding instructions byte for byte, as DiRT2VR does, so the game keeps the player's car under player control. iztok drove the car himself in a desktop benchmark start at Aspen. `tools/run_benchmark.py --direct-practice` uses it; findings, the ruled-out alternatives and open questions (session end, laps, opponents) are in `docs/research/direct-launch.md`. New native test `direct_practice_site`, including a check against the installed game. Not yet a launcher option; not tried in VR.
- Keep DiRT 3 running when its window loses focus (background play, as in DiRT2VR and GRID2VR). DiRT 3's focus-loss handler (RVA 0x296800, logged by the game as "APP focus lost") requests the pause menu and pauses a running replay; with `DIRT3VR_BACKGROUND_PLAY=1` the renderer replaces only those two instructions with NOPs in memory, after an exact byte check, and leaves the game's other focus and input notifications alone. Nothing changes on disk. `tools/run_benchmark.py --background-play` uses it and starts the game window without activating it; the renderer's present log now records whether DiRT 3 was in the foreground. A desktop benchmark run with the game in the background the whole time (203 of 203 present logs) completed normally. Not yet a launcher option. New native test `background_play_sites`, including a check against the installed game.
- Add a research-only renderer build (`build/native/bin/research/d3d11.dll`, never packaged) that applies an in-memory code change from `DIRT3VR_RESEARCH_PATCH` after its original bytes match, for bounded desktop experiments with `tools/run_benchmark.py --research-patch`. The shipped renderer doesn't contain this code. New native test `research_patch_parser`.
- Launcher polish (adapted from GRID2VR's selectable status text and DiRT2VR's launch button tips): the status line and the message above the buttons can be selected and copied (drag, Ctrl+A, Ctrl+C or right-click) to paste into reports; they wrap up to six lines, then scroll, and a refresh of the same message keeps the selection. Resting the pointer on a disabled Launch, Launch VR, Stop VR, Restore original files or Save settings button now says why it is disabled (for example that experimental VR must be enabled, DiRT 3 is still running, or files from an earlier session need restoring); the reason is also the button's accessible description. **Diagnostic logging** finally has a place under Settings → Diagnostics, with a line saying what it records (a metadata-only presentation trace for up to 180 seconds or 512 records); it had no visible control since 0.1.19. 18 new launcher checks pass under Wine.
- Split the instructions into topics (adapted from DiRT2VR's instructions view and GRID2VR's per-section links): Getting started, Startup logo skipping, Experimental VR, During a session, Graphics, Controls and recentering, VR runtime and headset check, Settings, profiles and defaults, Restoring original files, Updates, Files and logs, and About DiRT3VR. About shows a topic list beside the text, or a dropdown above it when the window is narrow. Every tab has an Instructions link that opens its own topic, and the Startup, VR runtime, Diagnostics and Implementation status sections link to theirs. The topics were rewritten from the old single text and now cover the newer features (notification-area sessions, OpenXR add-ons, renderer diagnosis, restore choices, updates, defaults); dynamic details such as the recenter key and the preferences folder stay current. 38 new launcher checks pass under Wine (topics, links on every tab, list/dropdown switching, unknown topics); the wide and narrow layouts were checked on screen.
- Add **Restore graphics defaults…** (Graphics) and **Restore launcher defaults…** (Settings), adapted from GRID2VR and the DiRT2VR request for per-tab defaults. Graphics defaults set desktop display mode, resolution and VSync to Use game setting and VR render scale to 100%. Launcher defaults reset every launcher choice (appearance, VR runtime, logging, startup, graphics, recenter key and controller button) but keep the VR runtime locations and the experimental VR opt-in. Both ask first (saying when unsaved edits would be replaced), skip the question when the controls already use the defaults, and, like loading a settings profile, change only the controls: Save settings or Launch keeps the result. Game files and settings profiles are not touched. 16 new launcher checks pass under Wine (the two resets, what they keep, Cancel, the unsaved-edit warning, nothing saved until Save settings).
- Fix VR sessions leaving DiRT3VR's files behind when DiRT 3 rewrote its graphics settings (#13ad25e5). On 31 Oct the game changed the resolution and refresh rate in its graphics file during VR; recovery treated that as a conflict and, because it checked the whole journal before restoring anything, also left the proxy `d3d11.dll`, the patched restrictions file and the startup file in place, so even Steam launches loaded the VR proxy. The refusal only appeared as a launcher notice. Now:
  - Every file that still holds exactly what DiRT3VR wrote is restored, even when another file conflicts. A file changed outside DiRT3VR is left as it is, the journal stays pending for it and the message names it. Damaged recovery evidence (journal or backups) still stops everything before any file is touched.
  - When DiRT 3 changed only values the VR session owns (resolution, aspect, refresh rate, window mode, VSync, anti-aliasing, DirectX 11, stereo, serial rendering), the original graphics file is restored automatically; the game's version is kept as `game-written-*.xml` in the recovery folder.
  - If other graphics settings changed too, **Restore original files** asks: keep the changes made in DiRT 3 (the session's values go back to the ones from before VR), restore all settings from before the session, or decide later. Only the second is offered when the file is no longer valid settings XML. The elevated recovery worker takes the same choice (`--graphics-choice`).
  - Every restore result goes to `logs\recovery.json`; a refused post-exit restore is also recorded in the launch status, and a failed elevated worker now reports its own reason instead of a generic message.
  45 new launcher checks pass under Wine, including the real graphics files from the 31 Oct session (kept byte for byte as a fixture; that session now restores every original exactly without asking), keep/restore/decide later, invalid XML, restrictions and proxy conflicts, damaged backups, the worker choice and messages, logging, and the Restore original files dialog flow). Existing tests that asserted the old all-or-nothing rule were changed, including `StartupRecoveryTests`, which needs the local DiRT 3 fixture and could not run here.
- Pass the recenter request path to the VR renderer only when a controller/wheel button is bound, so sessions without one don't poll for the file every frame (reported by ClaudeCode in review).
- Keep the session going when the launcher window is closed (adapted from DiRT2VR and GRID2VR). Closing the window during a VR or startup-skipping session, or while a file operation runs, no longer minimizes it: DiRT3VR hides and shows an icon in the notification area with **Open DiRT3VR**, **Stop VR** and **End session**. When DiRT 3 closes and the original files are restored, it says so and quits by itself; if recovery fails or needs attention, or an operation started before closing fails, the window comes back with the problem. Starting DiRT3VR again (for example from the Start menu) shows the running window instead of opening a second launcher. The footer gets a separate **Stop VR** button, which turns the headset off and keeps DiRT 3 running on the monitor (with the VR graphics settings until it closes); the old **Stop VR / quit** is now **End session**. Windows sign-out and shutdown are no longer held up by a session: recovery stays journaled for the next start. 33 new launcher checks pass under Wine (close decisions, single-instance signalling between launchers, the icon and its menu, footer layout with Stop VR at three widths, reopening and quitting the window). Not yet tried on Windows with a real session; the notification icon and balloon were not visible under Xvfb.
- Document DiRT 3's graphics settings for the coming launcher graphics controls: `docs/research/graphics-options.md` covers where the game keeps them, every quality option with its written values, the five presets with their DirectX 11 variants, and what the restrictions file enforces. `tools/data/graphics-options.json` is the machine-readable catalog, generated from the game's own `hardware_settings_options.xml` by `tools/extract_graphics_options.py` (`--check` compares it with an installed game). DiRT 3 has no anisotropic filtering or bloom setting, so those need renderer work. Static reading only; no game launch. 4 new Python tests, including a check against the installed game.
- Recenter with a controller, wheel or button-box button too (adapted from GRID2VR). Under **Controls → VR and seat controls**, **Bind a button…** takes the next fresh button press or D-pad / hat direction on any connected device (diagonals don't count); **Clear** removes it. During VR the launcher watches that button without taking the device over, only while DiRT 3 is in front, and fires once per press (a button already held when DiRT 3 gains focus doesn't count; an unplugged device is found again). Each press creates the session's `recenter.request`, which the VR renderer removes and acts on, logging "XR recenter requested by controller/wheel button"; `recenter-controller.json` in the session logs counts the requests. The binding is kept in launcher settings only: device identities are machine-specific, so settings profiles never carry it and loading one keeps this PC's binding. 46 new launcher checks pass under Wine (detection, validation, press edges, the watcher with a scripted device, settings and profiles, the Controls row, the bind dialog); `src/game_vr.cpp` compiles with MinGW. Not yet tried with a real device in the headset.
- Explain why VR didn't start when DiRT 3 ran without DiRT3VR's renderer (adapted from GRID2VR). Once the game window exists, the launcher checks which `d3d11.dll` DiRT 3 loaded and names the cause with a fix: the file was removed (usually antivirus), replaced by another mod, Windows' 'prefer system32 images' rule applied, or Windows used its own copy (usually a missing Visual C++ 2015-2022 x86 runtime). The result is saved as `renderer-load.json` in the session logs. Before staging a VR launch, it refuses when exploit protection forces that rule for `dirt3_game.exe` or the launcher itself carries it, which DiRT 3 would inherit. 18 new launcher checks pass under Wine (cause order, messages, MitigationOptions bits, module listing and diagnosis against a real process).
- Handle OpenXR add-ons that stop VR from starting (ported from DiRT2VR). When an add-on without a working 32-bit library, such as ReShade or the OBS mirror, makes the runtime check fail, Launch VR and Check runtime and headset name it and offer **Disable and retry**. The add-on is turned off for DiRT 3 only, through its own `DISABLE_XR_…`/`DISABLE_OPENXR_…` switch, for both the probe and the game; other apps keep it. A choice can be remembered and undone under **Settings → VR runtime → OpenXR add-ons…**, and needs a new approval if the add-on's registration changes. Only a failure the loader attributes to one registered add-on with its own switch is offered; anything else is explained, not bypassed. The runtime probe now runs with OpenXR loader warnings, and its JSON line is picked out of the loader's messages. 51 new launcher checks pass under Wine (manifests, the pinned loader's failure text, resolution and shared-switch refusal, approve/remember/cancel/changed-registration flows, the prompt, the add-on list and the game's environment).
- Check for updates from the launcher, as in DiRT2VR and GRID2VR. **?** now opens About, updates and instructions, with Check for updates, Download and install, Release notes and **Include experimental releases**; experimental builds also get **Return to stable…** (preferences are backed up first). Installed versions check GitHub Releases once at startup and show **Update available** next to **?**; development and test builds don't check on their own. Only the exact `DiRT3VR-<version>-Setup.exe` asset of `preseznik/DiRT3VR` is downloaded, verified against GitHub's SHA-256 digest and size, then Setup runs for the current game folder after any pending game files are restored, and the launcher closes. While the repository is private the check reports no public releases yet. Ported from DiRT2VR's updater; 55 new launcher checks with a simulated GitHub pass under Wine. Release requirements are in `docs/development.md`.
- Add **Controls → Live input and force feedback…**: every wheel, pedal set, shifter and controller with live axis meters, buttons and hats, plus **Test rumble** / **Test force feedback** per device. A failed test names the refusing Windows call or every refused effect shape. Devices are read without taking them over, and nothing changes DiRT 3's controls. It uses GRID2VR's game-independent x64 controller reader (`tools/driving-input`, copied unchanged with a `source.json` receipt), built by `tools/build.ps1` and shipped as `payload/driving_input.dll`; `verify_package.py` checks it is an x64 DLL. 19 new launcher checks pass under Wine with and without the DLL (with a MinGW build of it, whose own axis test also passes); the window was checked on screen with a stand-in DLL reporting a wheel and an Xbox pad. Not yet tried with real devices, and the MSVC build of the DLL is untested.
- Choose the recenter key under **Controls → VR and seat controls**: click the key and press any key, optionally with Ctrl, Alt or Shift; Esc cancels. F10 stays the default. Alt+Tab, Alt+F4 and Ctrl+Alt+Delete are reserved, and the exact modifiers must match, so Ctrl+F10 doesn't trigger a plain F10 binding. The key is saved with launcher settings and in settings profiles, which now export schema 3 (schema 1 and 2 still load, with F10). The VR renderer reads it from DIRT3VR_RECENTER_KEY and logs the bound key. DiRT 3 still receives the key, so pick one the game doesn't use. Controller and wheel buttons follow later. Adapted from GRID2VR's recenter shortcut; 39 new launcher checks and a new native test (`recenter_shortcut_binding`), run with g++ here; `src/game_vr.cpp` compiles with MinGW apart from its existing MSVC-only exception blocks. Not yet tried in the headset.
- Support read-only game files instead of refusing to launch. Players often make the graphics settings file read-only so the game stops resetting it. Desktop graphics choices, VR sessions and startup logo skipping now change such files and leave them read-only; the recovery journal records each original's read-only flag and restoration puts it back, also when it was lost during play. File replacement keeps the replaced file's attributes, because Windows refuses to replace a read-only file even for administrators (adapted from DiRT2VR). Journals written by older versions still recover. 12 new launcher checks; under Wine, which refuses read-only replacement like Windows, all launcher test groups pass except the two that need the local DiRT 3 startup fixture.
- Make the difference between saving a profile and Save settings clear. The profile buttons are now **Save as new profile** and **Overwrite profile…**. The section text, tooltips, confirmation and status messages say that they change only the profile, while **Save settings** (like Launch) keeps the current controls as the launcher's settings and never changes a profile. Help and README updated.
- Harden `ProfileTests.RunUi` against its intermittent failure ("Sequence contains no elements" after Save as new); the exact cause was not reproduced. `Button.PerformClick` silently does nothing unless the button can be selected, so the test now brings the button's tab forward, waits up to two seconds for it to become clickable, and otherwise fails naming the hidden or disabled control. Failures after a click include the panel's message, which is where the profile handler reports store errors. New checks confirm Save settings leaves every profile file unchanged and that both status messages say what was not saved.
- Compiled on Linux with Windows targeting (launcher and launcher tests, no warnings) and ran the launcher tests with `--ui` under Wine 9.0. Every group passed except the two that need the local DiRT 3 startup fixture (`StartupTests.Run` and `RunWindowLifecycle`); `ProfileTests.RunUi` passed in 5 of 5 runs without needing the click wait. Unchanged `main` failed the same groups in the same environment. A Windows run with the fixture is still to do.

## 0.1.20 — installer, F10 recenter, race display panel, collapsible sections and safer scrolling

- Add `DiRT3VR-0.1.20-Setup.exe`, an Inno Setup installer like DiRT2VR's and GRID2VR's. It detects the DiRT 3 folder, installs the launcher into its `DiRT3VR` subfolder with a Start menu shortcut and changes no game files. Setup, the launcher, its windows and shortcuts use the new DiRT3VR badge icon. Install and uninstall refuse while DiRT3VR or DiRT 3 runs, or while file recovery is pending. The portable ZIP remains available.
- Collapse or expand any launcher section by clicking its heading (▾/▸), or with Space/Enter when it has keyboard focus. Collapsed sections are remembered with the window size. Adapted from GRID2VR; every launcher section uses these headings, and tests reject a section built without one.
- Move three missed race display (OSD) shaders onto the VR HUD panel. They were drawn into both eyes and showed as a head-locked rectangle at the top of the headset view. The user confirmed in a VDXR race that the rectangle is gone.
- Move VR recentering from F9 to F10 to match the other VR projects. It still applies only while DiRT 3 is the foreground application. Update the launcher help text and README.
- Make mouse-wheel scrolling over the VR render scale slider and closed dropdowns scroll the page instead of silently changing settings. Clicking, dragging and keyboard editing still work; an open dropdown keeps wheel selection. Adapted from GRID2VR; every launcher window is protected.
- 999 launcher checks pass, including section collapse and its remembered state, the installer commands, the embedded icon, high-resolution wheel deltas, Windows scroll settings, nested pages and every slider and dropdown in both appearance styles.

## 0.1.19 — startup logo option

- Add an experimental, opt-in startup logo skip for desktop and VR launches. Keep copyright, Press Start and other game movies.
- Back up and temporarily change only the supported original startup definitions. Restore them after game exit; preserve external edits and recovery evidence on conflicts.
- Keep the launcher running until desktop startup-file recovery finishes, without injecting the VR proxy into desktop play.
- Include the choice in schema-2 settings profiles; continue reading schema-1 profiles with skipping disabled.
- 827 launcher checks pass, including desktop exit restoration, staged/partial recovery and profile migration. The user confirmed desktop and VDXR logo skipping with copyright, Press Start and tracked menus intact. Desktop restored automatically; VR required review of the known graphics resolution/refresh rewrite. All original files are restored.
- Leave background play unexposed: DiRT2VR's implementation needs game-specific native patches, and a DiRT 3 equivalent remains unverified.

## 0.1.18 — named launcher settings profiles

- Save, load, update, rename and delete named profiles under Settings; import and export versioned JSON files.
- Keep profiles separate per installation. Include desktop, VR, appearance and logging choices while preserving game saves and local runtime paths.
- Confirm before replacing unsaved controls, updating a saved profile or deleting it. Loading stays unsaved until Save settings or Launch; imports never load automatically.
- Refuse invalid imports and stale updates; give duplicate imports a new name. Preserve healthy profiles when another file is corrupt.
- 735 launcher checks pass, including actual profile button handlers and Modern/Classic layouts. All 22 package files, ZIP contents and the read-only installation check pass.
- Retain the accepted native 0.1.16 runtime and the known desktop fullscreen Alt+Tab resolution issue. No game launch is needed for this settings-only feature.

## 0.1.17 — desktop display controls

- Add desktop resolution, VSync, Windowed, Fullscreen and Borderless choices under Graphics. Defaults preserve existing game settings.
- Borderless fills the main display at its current resolution, checks the launched game's window ownership, and restores its frame if placement fails or startup is interrupted.
- Apply desktop choices on Launch, preserving unrelated graphics options and an exact backup. Save settings only saves launcher preferences.
- Keep VR graphics separate and retain the native runtime from the accepted 0.1.16 package unchanged.
- 660 automated launcher checks pass. The user confirmed Borderless and Windowed 1920×1080, Alt+Tab and normal quit. VSync On was applied and retained in the game settings. A foreground launch also reached Fullscreen 1920×1080, but returning from Alt+Tab switched to native 4K; retaining the selected fullscreen resolution remains unresolved. Every game launch requires user approval.

## 0.1.16 — shadow-pass stability investigation

- Replace the frontend shadow pass's broad graphics-state reset with explicit compute-binding cleanup, matching the GRID2VR approach.
- Record the graphics device's removal reason if VR stops after device loss.
- 0.1.15 failed during intro; the 0.1.14 control remained stable. The user subsequently confirmed a stable 0.1.16 session and fixed triangle-shadow tracking. Racing HUD was not retested; the user accepted proceeding.

## 0.1.15 — frontend triangle shadows

- Adjust the frontend triangle-shadow mapping for each tracked eye to address shadows sliding with head movement. Headset validation is pending.
- Restrict the correction to the verified frontend shadow shaders and matching native matrix; preserve all other shadow constants and graphics bindings.
- Retain the intro handoff, HUD proportions and transitions confirmed by the user in 0.1.14.

## 0.1.14 — intro handoff and HUD proportions

- Keep the startup presentation on a shared screen through the final copyright splash, ending it on the game's native legal-screen exit instead of the first frame without video.
- Preserve racing HUD proportions in the shared widescreen canvas, including frames that mix menu elements and racing instruments.
- Retain the 0.1.13 transition change. The user confirmed complete intro/splash playback, normal HUD proportions and clear menu transitions in 0.1.14.

## 0.1.13 — intro presentation and stereo transitions

- Submit interactive VR frames at Present so recognized intro videos drawn after the 3D scene can appear on a shared widescreen panel.
- Replace the captured screen-transition overlay with a direct cut during stereo rendering. Its cached screen image was being blended into both eyes; native loading pauses may still occur.
- Preserve the accepted menu canvas, letterbox removal and racing HUD. Both fixes require a fresh headset check.
- Keep one XR frame per game frame, with current-image ownership and cleanup of abandoned pending frames.

## 0.1.12 — automatic movie and transition diagnostics

- Automatically trace the native movie and final-composite paths, including rendering outside stereo eye passes, when diagnostic logging is enabled.
- Bound metadata tracing to 180 seconds and 512 records, with no GPU readbacks or extra user key presses.
- Preserve the user-accepted 0.1.11 menu/bar/HUD rendering. Black intro videos and doubled transition freezes remain unresolved pending this evidence.

## 0.1.11 — menu canvas and letterbox correction

- Restore the full widescreen canvas for the captured DiRT 3 menu camera before vertex clipping. Expand the VR panel sideways while retaining the physical size of already-visible interface elements.
- Remove only the captured opaque-black overscan letterbox geometry; keep black menu panels, fades and translucent artwork.
- Validate camera data without GPU readback during normal play, and restore all changed graphics bindings after each draw.
- Menu/bar corrections require a new headset check, including the previously accepted racing HUD. Transition freezes/doubling remain unresolved after the 0.1.10 test.

## 0.1.10 — transition history and HUD diagnostics

- Reset both eyes' temporal history after camera cuts, recentering and interrupted stereo frames. The subsequent user test still reported transition doubling.
- Add bounded timing logs and an explicit F8 single-frame HUD diagnostic for the remaining menu crop and floating bars. No readback during ordinary play.
- 0.1.9 racing HUD readability is user-confirmed; menu cropping and bars are not fixed in this build.

## 0.1.9 — experimental HUD/menu panel

- Capture recognized 2D interface draws separately from stereo game images and present one transparent VR panel.
- Keep the captured interface visible in the desktop mirror and retain native stencil masks.
- Bound panel size and memory, recenter with F9, and discard incomplete or stale interface frames.
- Shader coverage and readability across real menus and races still require headset validation.

## 0.1.8 — native scene resolution enforcement

- Apply the selected VR render size when DiRT 3 creates or resizes its render buffers, after it validates its display modes.
- Guard the exact game build, call sites, renderer ownership and dimensions; roll back size changes when allocation fails.
- Native creation, resize, recreation and refusal tests pass. Live resolution and headset acceptance remain pending.

## 0.1.7 — one headset-relative resolution control

- Replace headset output scale with one VR render scale slider, following GRID2VR.
- Use the detected headset recommendation as 100%; keep output fixed while scaling the scene.
- Show headset identity, output and render dimensions, and the supported maximum.
- Refresh detection before VR launch and reject stale or unsupported dimensions.
- Restore original graphics settings after custom-resolution sessions.
- New resolution behavior passes automated tests; live game/headset acceptance remains pending.

## 0.1.6 — first launcher parity additions

- Remember launcher window size and maximized state, adjusted for screen size and DPI.
- Add a 25–100% headset output scale slider in Graphics; keep the existing 50% default.
- Show the build version in the window title.
- Preserve unsaved game settings when closing or switching appearance.
- Scene resolution remains 1280×720; higher output scale alone does not add scene detail. New scale settings have automated coverage and still need headset acceptance.

## 0.1.5 — local VR activation fix

- Fix the cockpit check rejecting the game's 2,000-to-1,000 far-distance change.
- Enable stereo and headset tracking for all valid rendered 3D camera views, including cockpit, bonnet, bumper, chase, reverse and replay.
- Keep renderer, displayed-camera, frame-ownership and restoration guards in place; camera names no longer control stereo activation.
- Refresh the second eye's temporal history after identified camera transitions.
- User confirmed 0.1.5 working in VDXR; broader camera/effect, stability and SteamVR acceptance remain open.

## 0.1.4 — local cockpit diagnostic build

- Distinguish virtual-screen output from stereo cockpit frames in launcher status.
- Capture bounded camera-field differences when the selected cockpit fails the display match.
- Keep camera acceptance unchanged pending evidence from a steady cockpit test; this build does not claim to fix the reported flat-screen output.

## 0.1.3 — local launcher fix

- Fix the `0xEF` JSON error that prevented enabling experimental VDXR in the packaged launcher.
- Accept UTF-8 BOM-prefixed data files and write portable package JSON without a BOM.

## 0.1.2 — local experimental VR

- Add opt-in VDXR stereo rendering with head tracking, asymmetric projections and F9 recentering.
- Add a virtual screen for views outside the verified cockpit identity and reversible VR launch configuration.
- Preserve native simulation timing while rendering two eyes, and restore owned camera fields after failures.
- Keep SteamVR interactive launch disabled while an upside-down presentation report is investigated.
- Interactive driving, pause/replay transitions, HUD and live shutdown acceptance remain pending.

## 0.1.1 — local launcher fix

- Keep startup progress active through DiRT 3's loading splash until its actual render window appears.
- Bring the game forward when the launcher still has focus, and provide Show game for an existing instance.
- Keep startup errors visible outside the scrolling tabs and record startup outcomes and exit codes.

## 0.1.0 — local research build

- Add a yellow DiRT3VR launcher with desktop launch, runtime checks and independent settings.
- Add a fingerprint-guarded x86 D3D11 diagnostic proxy and a session-free OpenXR probe.
- Add bounded benchmark deployment and recovery, native forwarding tests and launcher checks.
- Keep VR launch unavailable while the DiRT 3 stereo render boundary is being validated.
