# Source baseline

Implementation began on 2026-10-28 from DiRT2VR `main`, commit
`4de96cc56843599b175e18c7f323b474ae1dd9a2`, at
`F:/Coding/Codex/Mods/DiRT2VR/artifacts/prize-cars`.
An elevated read-only `git status --short --branch` confirmed a clean tracked
working tree before reuse. The original checkout is not modified.

The initial supported game is Steam DiRT 3 Complete Edition, app 321040,
build 3617310. `dirt3_game.exe` is a 14,578,688-byte x86 PE with SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.
Its PE large-address-aware flag is already set.

Ordinary offline PE parsing/disassembly established `LoadLibraryA("d3d11.dll")`
at VA `0x7c8e9b` and lookup of `D3D11CreateDevice` at `0x7c8f63` (image base
`0x400000`). These are static observations, not proof of execution or stereo.
The previous REA Ghidra overview request timed out after 300 seconds; that
session was closed. No decompiler result was obtained from that request.

The architecture reuses the D3D11 export-forwarding design, independent OpenXR
components, and WinForms appearance/recovery patterns. DiRT2 game offsets,
shader identities, native camera classification, LAN/profile code, executable
patches and content converters are not portable assumptions.

Native dependency source pins and per-file hashes are recorded in `.deps/*.source.json`
by `tools/bootstrap.ps1`. Packaging retains dependency notices. This baseline
does not establish either headset runtime's live acceptance.

## Launcher parity batch, 0.1.6

Reconfirmed the same reference commit before reading `WindowPreferences.vb`,
`MainForm.vb`, `Core.vb` and `Session.vb`; `git status --short` for those paths
reported no working-tree differences. Adapted window-size/maximized persistence
to DiRT3VR's per-install `SafeFiles` storage, keeping it separate from game settings.
Reused the reference `ValueSlider.vb` component with DiRT3VR's yellow/default marker
and existing high-contrast colors. Qualified `System.Drawing.Graphics` to avoid
the local graphics-configuration helper's name collision. No reference files or
DiRT2 game hooks were changed or imported.

## Single-slider reference, 0.1.7

Read GRID2VR at `cc481c679ffa8ee0391291d001ad83349388b4ca`. Read-only status checks
reported no differences in the inspected resolution/UI/runtime files. Adapted
its headset-relative model: a 100% scene baseline, fixed recommended-size output,
per-launch re-query, stale-headset refusal and dimensional summaries. DiRT3 uses
its existing VB launcher and JSON runtime probe, with stricter 4096 dimensions
and a 512 MiB XR color budget. GRID2's native scene-size enforcement offsets were
not imported; actual DiRT3 scene-size acceptance remains a live gate.

## Native scene-size correction, 0.1.8

After the approved 0.1.7 retry proved a 1280x720 clamp, read GRID2VR's
`src/scene_resolution.cpp/.h` at the same commit. Adapted six-word transactional
sizing and renderer ownership. DiRT3's offsets, creation/resize callers and
stack-local recreation output were established independently from its exact
fingerprinted executable. `scene_binary_contract` checks the loaded-byte guard
definitions against that file. Synthetic failures and ownership tests pass;
the new binary has no live acceptance yet. Reference projects were read only.

## HUD/menu separation, 0.1.9

Adapted DiRT2VR's `HudCapture` and scoped draw routing from
`artifacts/prize-cars/src/hud_capture.cpp/.h` and `trace.cpp`. DiRT3 shader
identities come from its own fingerprinted `ui.pssg`, not copied DiRT2 hashes.
Changes include eight-bit capture alpha, frame-scoped backbuffer ownership,
native stencil-write preservation, stricter shared-shader depth/UAV guards and
explicit exception restoration. DiRT3's existing XR transaction now supports
an optional third swapchain and transparent panel; the existing isolated
presenter handles straight-alpha conversion and the desktop mirror composite.
No converter dependencies, region coordinates or default HUD hiding were copied.
Reference files were read only. Live acceptance is separate from synthetic tests.

## Desktop display controls, 0.1.17

Read DiRT2VR at `9631f0687c9a1da1b1202eefe5e899f0e5e79bb7`.
`artifacts/prize-cars/launcher/BorderlessWindow.vb` had no working-tree changes.
Adapted its frame removal, asynchronous placement, verification deadline and
rollback. DiRT3 uses its own launched PID/start time and `NeonClass_41` filter;
it never scans for or operates on DiRT2 processes. Added cancellation cleanup.

Read GRID2VR at `29a4969a8775dbba17252c0bd2923a199fc2036d` for process ownership
and topmost rollback handling. Its `launcher/DesktopWindow.cs` had one local
change: desktop ownership also requires the `desktopBorderlessRequested` journal
flag. That difference was inspected before reuse. DiRT3 uses an explicit
desktop-only launch option instead of GRID2's session journal/watcher. Neither
reference repository was modified.

The installed DiRT3 Documents XML supplies `graphics_card/resolution` fields
`width`, `height`, `fullscreen` and `vsync`. Its baseline was 822×656, windowed,
VSync 0, MSAA 4×, refresh 120 and aspect auto. The shipped system XML independently
uses the same fields. Desktop writes are persistent, opt-in, atomic and backed
up by original content hash; VR keeps its existing reversible transaction.
No new native hooks, converter dependencies or renderer changes are included.

## Named launcher settings profiles, 0.1.18

Read DiRT2VR at `9631f0687c9a1da1b1202eefe5e899f0e5e79bb7`.
`artifacts/prize-cars/launcher/Profiles.vb` and `ProfilePanel.vb` were clean.
Those files currently manage careers, offline-provider mounts and game-save
details, rather than portable launcher settings. The plan's storage-reuse
assumption therefore did not apply. Reused the panel's explicit selection/action
pattern, wrapping buttons, inline errors and installation refresh behavior;
implemented a small independent settings store with no career dependencies.
The reference repository was read only. No DiRT2 process was launched.

## Startup conveniences, 0.1.19

Read DiRT2VR at `9631f0687c9a1da1b1202eefe5e899f0e5e79bb7`.
`StartupMovies.vb`, `BackgroundPlayLaunch.vb`, `src/background_play.cpp` and
`src/startup_hooks.cpp` were clean. Adapted the named-logo-to-waypoint approach
and journal-before-deployment ownership. DiRT3 uses its independently decoded
`system/states.bin` fingerprint and eight bounded 32-bit field edits. No DiRT2
offsets, full XML converter, career handling or project dependency was imported.

DiRT2 background play uses guarded native patches at its own focus-loss pause
instructions. No equivalent setting was established in the bounded search of
DiRT3's 32 shipped system XML files, current Documents graphics XML and current
runtime source. That feature remains unimplemented pending DiRT3-specific
handler and gameplay analysis. Neither reference game nor the target game was
launched during this research. The reference checkout was not modified.
