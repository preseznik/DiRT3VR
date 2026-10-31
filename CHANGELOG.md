# Changelog

## 0.1.20 — installer, F10 recenter, race display panel and safer scrolling

- Add `DiRT3VR-0.1.20-Setup.exe`, an Inno Setup installer like DiRT2VR's and GRID2VR's. It detects the DiRT 3 folder, installs the launcher into its `DiRT3VR` subfolder with a Start menu shortcut and changes no game files. Setup, the launcher, its windows and shortcuts use the new DiRT3VR badge icon. Install and uninstall refuse while DiRT3VR or DiRT 3 runs, or while file recovery is pending. The portable ZIP remains available.

- Move three missed race display (OSD) shaders onto the VR HUD panel. They were drawn into both eyes and showed as a head-locked rectangle at the top of the headset view. Needs a VR race to confirm.
- Move VR recentering from F9 to F10 to match the other VR projects. It still applies only while DiRT 3 is the foreground application. Update the launcher help text and README.
- Make mouse-wheel scrolling over the VR render scale slider and closed dropdowns scroll the page instead of silently changing settings. Clicking, dragging and keyboard editing still work; an open dropdown keeps wheel selection. Adapted from GRID2VR; every launcher window is protected.
- 971 launcher checks pass, including the installer commands, the embedded icon, high-resolution wheel deltas, Windows scroll settings, nested pages and every slider and dropdown in both appearance styles.

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
