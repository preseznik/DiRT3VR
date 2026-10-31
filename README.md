# DiRT3VR

DiRT3VR is a local development build for **Steam DiRT 3 Complete Edition**.
Version 0.1.19 adds an opt-in **Skip startup logo movies** control on Launcher.
It replaces only the AMD and DiRT 3 logo states; copyright and Press Start remain.
The original startup file is backed up and restored after desktop or VR exit.
Keep the launcher open for recovery. Startup skipping passed attended desktop
and VDXR checks. VR cleanup required review of the game's known graphics XML
rewrite; all originals were restored. Leave the experimental option off for
multiplayer until compatibility is checked.
Version 0.1.18 adds named launcher settings profiles under Settings: save, load,
update, rename, delete and JSON import/export. Profiles contain desktop and VR
choices, appearance and logging; they leave game saves and local paths alone.
Version 0.1.17 adds desktop resolution, VSync and windowed/fullscreen/borderless
controls. The user confirmed Borderless and Windowed 1920×1080, Alt+Tab and normal
quit; VSync On was applied and retained in the game settings. Fullscreen starts
correctly at 1920×1080 when launched from the foreground, but the game switches
to the display's native 4K resolution after Alt+Tab back. This remains a known
desktop limitation. It retains one **VR render scale** slider relative to the connected
headset's OpenXR-recommended resolution. The default is 100%. It replaces the
separate output-scale control from 0.1.6.
Stereo follows **every valid rendered 3D camera view**, including cockpit,
bonnet, bumper, chase, reverse and replay. The user confirmed basic VDXR driving
with version 0.1.5, native headset resolution in 0.1.8, and the corrected intro,
HUD and menu presentation in 0.1.14. Broader visual coverage remains open.
SteamVR interactive launch remains disabled while an upside-down presentation
report is investigated.

## Use the launcher

1. Run `DiRT3VR-<version>-Setup.exe`, keep the detected DiRT 3 Complete Edition
   folder or browse to the one containing `dirt3_game.exe`. Setup installs the
   launcher into its `DiRT3VR` subfolder and adds a Start menu shortcut; it
   changes no game files. Close DiRT3VR first and finish any pending
   **Restore original files** before upgrading or uninstalling. Alternatively,
   extract the portable ZIP to a writable folder.
2. Open DiRT3VR and select your DiRT 3 Complete Edition folder if needed.
3. Use **Launch** for ordinary desktop play through the game's own menus.
   **Startup → Skip startup logo movies** is off by default. When enabled, it
   applies to desktop and VR launches. Closing the launcher during a session
   with temporary startup changes minimizes it until the game exits and the
   original file is restored. After an interrupted session, use this version's
   **Restore original files** before launching again. Other movies remain.
   Progress stays open through the loading splash until the actual game window
   appears. If it is behind another application, use **Show game** in the same
   footer position. Startup errors remain visible above the footer and are
   recorded under **Open logs**.
   Under **Graphics → Desktop**, choose a resolution reported by Windows,
   display mode and VSync. Every control defaults to **Use game setting**.
   **Borderless** uses the main display's current resolution; the separate
   resolution choice is retained for windowed/fullscreen use. Keep the launcher
   open until startup finishes so it can apply and verify window placement.
   Desktop choices apply on **Launch** and persist in the game's graphics file.
   **Save settings** only saves launcher preferences. Exact originals are kept
   under `%LOCALAPPDATA%\DiRT3VR\<install-id>\desktop-backups`; unrelated graphics
   options are preserved. **Restore original files** handles temporary sessions,
   not these saved desktop preferences. VR keeps its own graphics settings and
   restores the saved desktop configuration afterward.
4. **Controls → Live input and force feedback** shows your wheel, pedals,
   shifter and controllers as Windows reports them and tests rumble or force
   feedback on each one. It reads devices without taking them over and changes
   no game or Windows setting; close DiRT 3 before testing force feedback.
   **Settings** provides Modern/Classic appearance and runtime selection.
   **Settings profiles** saves named copies of the current controls per installation.
   Enter a name and choose **Save as new profile**, or explicitly **Overwrite profile**.
   Both change only the profile. **Save settings** in the footer is separate: it
   keeps the current controls as the launcher's settings, as Launch does, and never
   changes a profile.
   Selecting a profile does nothing until **Load**; loading asks before replacing
   unsaved edits and does not save preferences until **Save settings** or Launch.
   **Rename** uses the Name field. **Delete** removes only the selected profile.
   **Import** adds a versioned JSON profile without loading it; duplicate names get
   a numbered suffix. **Export** writes the selected saved profile, excluding
   machine-specific paths. Profiles are kept in
   `%LOCALAPPDATA%\DiRT3VR\<install-id>\settings-profiles`.
   **Check runtime** queries your selected 32-bit OpenXR runtime; it does not
   start a VR session and a successful query does not mean VR driving is ready.
5. **Graphics** provides one VR render scale slider from 50% to 300%. Use
   **Refresh headset resolution** to see detected output and requested scene
   dimensions. At 100%, the scene matches the runtime's recommended width and
   height per eye. Output stays at that recommendation while the slider changes
   scene detail. The launcher rechecks the headset before every VR launch.
   Unsupported sizes are refused; the displayed maximum depends on the headset.
   Changes apply on the next VR launch.
6. Use **Save settings**, **Restore original files**, and **Open logs** from
   the persistent footer when needed.

Old output-scale preferences start at the new 100% render-scale default; they
are not reinterpreted as scene scale. Other preferences are preserved.

Closing the launcher remembers its window size and maximized state separately;
it does not save unsaved game settings. The title shows the build version.

Named settings profiles now export schema 3, including the startup choice and
the recenter key. Schema-1 and schema-2 profiles remain readable, with startup
skipping off and F10 for recentering where they lack those choices.

For experimental VR, connect the headset through Virtual Desktop, choose VDXR,
and enable the experimental VR option in Advanced. Close other VR games first.
Use **Launch VR** and select any camera through DiRT 3's native controls.
Rendered 3D scenes use stereo and headset tracking; loading screens, movies and
frames without a valid 3D scene use a virtual screen. Press
**F10** with the game focused to recenter; choose another key, optionally with
Ctrl, Alt or Shift, under **Controls → VR and seat controls**. DiRT 3 still
receives that key, so pick one the game doesn't use. Recognized 2D HUD/menu draws now use
an experimental transparent panel, 2 metres away. The usual panel is at most
1.6 metres wide and 1.8 metres tall. The verified widescreen menu canvas expands
sideways (up to 3.2 metres) while preserving the physical size and position of
already-visible interface elements. Recentering also moves the panel.
The captured interface also remains visible in the desktop mirror.

Version 0.1.11 corrects horizontal clipping for the menu camera identified in
F8 captures and removes its specific floating black letterbox geometry. Other
black menu backgrounds remain. The user confirmed the full menu text, absent
floating bars and readable racing HUD in cockpit and external cameras in 0.1.11.
Adjustable HUD placement, element hiding and seat adjustment are not implemented.
Version 0.1.13 submits recognized intro videos after their late native draw, on
a shared 16:9 screen. The captured screen-transition effect uses a direct cut
in stereo: it no longer overlays the same cached screen view in both eyes.
Game-loading pauses may still occur. The user confirmed visible videos and that
the doubled/frozen transitions seem gone in 0.1.13. Version 0.1.14 keeps the shared
screen through the final copyright splash, using the native screen's exit event,
and preserves racing HUD proportions when mixed with widescreen menu draws.
The user confirmed complete intro/splash playback, correct HUD proportions and
clear transitions in 0.1.14. Version 0.1.15 added a separate frontend
triangle-shadow camera mapping for each tracked eye.
The first 0.1.15 test lost its graphics device during intro; a 0.1.14 control
remained stable. Version 0.1.16 replaces the shadow pass's broad graphics-state
reset with explicit binding cleanup. The user confirmed a stable 0.1.16 session
and correct triangle shadows while turning and leaning. The racing HUD was not
retested in that run; the user accepted proceeding with its prior validation.
0.1.16 is the current working VDXR baseline.

Version 0.1.12 adds automatic diagnostic tracing for the remaining black intro
videos and doubled menu transitions. With diagnostic logging enabled, it records
movie shaders, final composites and draws outside the eye passes, along with
camera and texture metadata. It stops after 180 seconds or 512 records. It performs
no GPU readback and requires no F8 presses. The 0.1.12 run supplied the evidence
for the targeted 0.1.13 changes above.

When requested for diagnostics, press **F8** with the game focused to record one
frame in the session log folder, including a raw RGBA panel image. GPU readback
briefly pauses that frame; do not count that deliberate pause as a transition
failure. Captures are limited to three per process and 256 backbuffer draws each;
ordinary rendering performs no readback.

The launcher temporarily installs its proxy and patches the graphics and CPU
restriction files for DX11, native stereo off and serial rendering. It journals
the original bytes and restores owned files after the session. Externally edited
files are preserved and reported as a recovery conflict. Keep the launcher
running until the session ends. UAC may be needed to write the Steam folder.
Read-only game files are supported: the launcher changes them, they stay
read-only during play, and restoration puts back the original read-only flag.

The desktop launcher runs on 64-bit Windows. The game, graphics proxy and runtime
probe are 32-bit. Choose **VDXR** or **SteamVR** in Settings. VDXR uses Virtual
Desktop Streamer's 32-bit runtime directly; SteamVR uses `steamxr_win32.json`.
Browse to the selected runtime manifest if installed elsewhere. Selection applies
only to the helper/launch process and does not read or change the system OpenXR
default. Both selections support runtime checks; interactive VR is currently
restricted to VDXR.

## Current limits

- Normal desktop launch uses the original game's menus and saved controls.
- Intro/splash completion, HUD proportions and clear menu transitions were
  confirmed in 0.1.14; stable frontend shadow tracking was confirmed in 0.1.16.
  Shared shaders with scene depth,
  depth-writing geometry, additive effects and unrecognized shaders retain their
  original rendering; missed UI elements need further classification.
- Direct event selection and seat adjustment are not available in this prototype.
- Version 0.1.7's live test found that DiRT 3 replaced the requested resolution
  with 1280×720. Version 0.1.8 adds guarded native buffer sizing; 100% output
  and driving passed the live check. Other scales and resets remain untested.
  If DiRT 3 ignores or changes the requested size, VR stops with a resolution
  mismatch in the logs; quit to restore the original graphics settings.
- The game can rewrite resolution and refresh-rate XML during startup. If
  recovery reports a conflict, keep its backups and use **Open logs** for review.
- Scene and output dimensions are limited to 4096 per dimension. XR output is
  limited to 512 MiB of color textures and four swapchain images per eye.
- All 3D camera types use the same stereo path. Pause, replay and frontend
  scenes retain tracking when the game renders a valid main scene; those
  transitions still need visual acceptance.
- Headlights, mirrors, water, shadows, postprocessing and long sessions still
  require visual and gameplay validation.
- The diagnostic proxy is not installed by normal desktop launch.
- Other game editions and modified executables are not validated.
- Existing graphics proxies and conflicting externally edited files are preserved.
- No automatic update feed or public release is provided yet.

Developer build instructions and current measured status are in
`docs/development.md` and `docs/vr-prototype-status.md` in the source project.
