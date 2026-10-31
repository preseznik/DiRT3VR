# DiRT3VR implementation status — 2026-10-28

**Current VR progress is recorded in [VR prototype status](vr-prototype-status.md).
VDXR interactive driving, resolution and 0.1.14 intro/HUD/transition presentation
have user confirmation; SteamVR orientation and broader coverage remain open.
The material below preserves the
earlier 0.1.0/0.1.1 diagnostic-foundation status and its failed first gate.**

## Historical foundation status

**The launcher and native diagnostic foundation are implemented. The first
milestone's safe stereo-rendering gate has not passed, so VR launch remains
disabled. This is a portable local research build, not playable VR.**

Development stopped before adding stereo or parity features, as required by the
plan's first exit gate. Two concrete issues remain: the effective scheduling
mode is overridden, and rendering mutates shared resources whose complete
restoration has not been established.

## Launcher correction — 0.1.1

Following the reported disappearing startup popup, the packaged Launch button
was exercised directly. The original monitor treated the `InitialLoading`
splash as a ready game. The real `NeonClass_41` render window appeared roughly
ten seconds later. That premature completion and lack of a bring-forward action
could leave the user with no useful startup feedback.

The corrected launcher waits for the visible render window belonging to the
same game process and start time. It activates the game when the launcher/game
still owns foreground focus, offers **Show game** for an existing instance,
keeps errors above the footer on every tab, and always records startup results
and available exit codes. It does not steal focus after switching to another app.

374 launcher checks passed. A live test of the packaged **Launch** button kept
progress open during `InitialLoading` (0–8 seconds), switched to ready only when
`NeonClass_41` appeared (about 9.3 seconds), and offered **Show game** without
starting a second instance. The test requested normal closure afterward.
Evidence: `build/validation/desktop-launch-repro/packaged-button-fixed.json`.
This verifies desktop window startup, not driving or VR acceptance.

Updated local package:
`artifacts/packages/0.1.1-20261028-100253/DiRT3VR/DiRT3VR.exe`, with adjacent
`DiRT3VR-0.1.1.zip`. All 21 inventoried files and ZIP contents were verified.
Use this build instead of the earlier 0.1.0 launcher. No native proxy/config
deployment was involved in these launcher tests; the stereo gate is unchanged.

## Delivered

- Independent C++20/MSVC Win32 project using pinned MinHook and OpenXR sources;
  no converter, custom-content, save, LAN or executable-memory patch dependency.
- Production x86 `d3d11.dll` forwarding the system D3D11 exports. Exact executable
  SHA-256 guards gate diagnostic hooks. Unknown hosts retain ordinary forwarding.
  Synthetic fixture bypasses exist only in a separate, unpackaged test DLL.
- Opt-in D3D11 device, DXGI swapchain/Present/resize, and passive main-scene
  observation. The engine is called exactly once with its original arguments;
  no eye replay, camera writes or intrusive-list traversal is enabled.
- An x86 OpenXR probe that selects a manifest for its own process, checks the
  D3D11 extension, system, graphics requirements and stereo view configuration,
  emits JSON, and creates no session.
- VB.NET/.NET 10 WinForms x64 launcher with the five requested tabs, persistent
  footer, desktop launch, startup progress, independent preferences, diagnostics
  and recovery infrastructure. Yellow accent `#FFD54A`; light accent `#7A5900`.
  Modern/Classic switching keeps controls, handlers, unsaved values and selected
  tabs. Runtime choices are explicit VDXR and SteamVR, each using an x86 manifest
  without changing the system OpenXR registration.
- File ownership journals, foreign-proxy refusal, exact-hash recovery,
  conflict preservation and a bounded benchmark runner. Normal launcher desktop
  play does not install the proxy or change graphics files.
- Reproducible build/package scripts, dependency notices and package hashes.

The launcher exposes no working headset tracking, seat controls, direct event
selection, render scale or effect settings. Those implementations remain behind
the failed first gate. Graphics and Controls currently explain using the native
game menus. The disabled Launch VR action cannot be unlocked by editing the
capability JSON.

## Automated validation

| Check | Result and scope |
|---|---|
| Native CTest | 6 passed: production forwarding/refusal, real WARP swapchain operations, diagnostics off, probe error JSON, exports, game signatures |
| Launcher | 338 checks passed: settings/runtime isolation and migration, x86 manifest validation, recovery/tampering/conflicts, capabilities, and offscreen UI |
| Diagnostic runner | 12 tests passed: owned-file recovery, external edits and path refusal, XML rewrite handling, benchmark scene/configuration verification |
| Static evidence | 28 fingerprinted instruction ranges and 8 call sites verified against the supported installed executable |

Offscreen UI coverage includes narrow/wide layouts and Modern/Classic switches.
It does not establish attended 100/150/200% DPI, live high contrast, keyboard,
UAC or launcher-driven game acceptance. Native tests do not establish headset,
same-frame eye ownership, pose/projection correctness or XR session failure
handling; those features are not implemented yet.

Builds succeeded. NuGet advisory lookup emitted NU1900 because the feed was
unavailable; it was not a compile failure. Third-party CMake optional-component
warnings did not block the x86 D3D11/OpenXR targets.

## Live desktop evidence

The final diagnostic used the supported executable SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`
and proxy SHA-256
`4ea87f0a2160296a1c39507b76840d2cf20a80080afefde89c5b911f76568814`.

Run folder:
`artifacts/diagnostics/20261028-092210-d2488aa9`.

- The game wrote a completed DX11 benchmark for Aspen `route_0`, car `frx`,
  with 47,978 benchmark samples. The requested 1280×720 swapchain was observed.
- The runner requested window closure after result output; the owned game
  exited with code 0 at 126.09 seconds, without forced termination.
- The log reached 57,240 Presents. Across 478 sampled main scenes, each invoked
  the inner renderer once and advanced the render serial by one. No camera-byte
  change or main-view identity mismatch was reported.
- 296 sampled inner calls changed the prepared-list header. The first four
  bounded offset reports changed bytes `0x84..0x87`, consistent with the
  statically identified list-head writer. This does not prove every mutation
  or every pointee is known.
- All 956 sampled outer/inner scopes had the same Present count at entry/exit.
  This locates a useful pre-Present boundary; it is not proof of two-eye or
  simulation-state safety.

The hardware file must be placed in resolved Documents
`My Games/DiRT3/hardwaresettings`, not beside the executable. Earlier 60/90-second
runs exercised desktop rendering but did not establish a completed benchmark
or the requested graphics preset.

**The game changed the temporary configuration's `parallelUpdateRender` from
false to true, and the benchmark result also reported true.** The shipped CPU
restrictions set this value true for multicore profiles; they are a consistent
explanation, although their application was not traced live. The main-loop
consumer contains distinct true/false paths. Merely editing the hardware
configuration is therefore insufficient.

Recovery correctly refused the changed temporary file. Its complete generated
contents were retained, the single semantic change was reviewed, and all three
owned files were then removed using exact hashes. `reviewed-recovery.json`
confirms that every protected original executable/settings/save hash matches
its baseline. The original failed `run.json` remains intact as historical
evidence. The game-written benchmark result remains available in its benchmark
folder and in the local run evidence. No diagnostic proxy remains installed.

## Runtime availability

Both standalone probes succeeded outside the Windows sandbox:

| Selected runtime | Query result |
|---|---|
| VDXR | VirtualDesktopXR 1.0.10; Meta Quest 3; orientation/position tracking advertised |
| SteamVR | SteamVR/OpenXR 2.18.4 in Meta compatibility mode; orientation/position tracking advertised |

Both returned adapter LUID `0:100248`, matching the observed game device, and
minimum feature level 11.0; the game created an 11.1 device. Both recommended
3072×3264 per eye. No eye textures were allocated and no OpenXR session was
created. These queries establish runtime availability only. VDXR/SteamVR
interactive cockpit, lifecycle, effects and performance acceptance remain open.
Raw query receipts are in `build/validation/*-probe-normal-windows.json`.

Later checks through the packaged launcher exercised both runtime selections
and returned the clear `XR_ERROR_FORM_FACTOR_UNAVAILABLE` error for each: the
headset was no longer available to those queries. Availability is a snapshot,
not a persistent acceptance result. The launcher handled those failures without
starting the game or an XR session.

## Local delivery

The portable build is in
`artifacts/packages/0.1.0-20261028-093251/DiRT3VR`; open `DiRT3VR.exe` there.
The adjacent `DiRT3VR-0.1.0.zip` contains the complete package (47,817,307 bytes).
All 21 inventoried payload files and archive contents were hash-verified; the
launcher is x64, both native binaries are x86, and fixture binaries/source/logs
are excluded. The packaged install check passed against the installed game.
`validation.json` beside the ZIP records these checks and runtime responses.
No release was published, and the source DiRT2VR checkout remains clean.

## Remaining work, in order

1. Make the effective scheduling mode false after CPU restrictions, with an
   explicitly owned and reversible implementation. Repeat the bounded benchmark
   and verify both the effective setting and original-file restoration.
2. Establish the complete mutation set within the prepared scene's lifetime:
   list headers and nodes, owner flags, render serials, camera context, GPU state,
   target/resource ownership and failure unwind. Camera/header snapshots alone
   are insufficient. The caller releases prepared data after the outer returns.
3. Demonstrate two distinct camera/projection views from one simulation frame,
   inside that scope, with full restoration and unchanged animation timing.
   Reuse DiRT2VR's eye transaction after adapting the verified DiRT 3 layouts:
   cameras are 0x90 bytes here, not DiRT2VR's 0x70 bytes.
4. Only after that gate: port OpenXR session/frame/swapchain handling and cockpit
   tracking, then HUD/virtual-screen presentation, graphics/effects and recovery.
   Run attended acceptance on both VDXR and SteamVR before enabling Launch VR.
5. Add content/event/camera parity in the later independently tested increments
   described in the accepted plan. Publishing remains a separate action.

Detailed addresses, signatures and the reasoning behind these limits are in
[render-boundary.md](research/render-boundary.md) and
[observer-followup.md](research/observer-followup.md). The source baseline is in
[source-baseline.md](source-baseline.md). REA sessions are closed.
