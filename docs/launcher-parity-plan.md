# DiRT2VR launcher parity plan

Updated 2026-10-29. Work proceeds in small, independently reviewable batches,
starting with settings that need no new game hooks. No game launch is permitted
without the user's explicit approval for that launch.

## Reference and working baseline

- DiRT2VR reference: `F:/Coding/Codex/Mods/DiRT2VR/artifacts/prize-cars`, commit
  `4de96cc56843599b175e18c7f323b474ae1dd9a2`. The referenced launcher files were
  checked for working-tree changes before reuse; none were reported. Read-only.
- DiRT3VR 0.1.16 is the current user-confirmed VDXR working baseline. Preserve its all-camera
  stereo behavior, yellow appearance, per-install settings and owned-file recovery.
- Existing parity: Modern/Classic and Windows themes, responsive five-tab layout,
  persistent footer, desktop/VR launch, startup progress, runtime selection/probe,
  logs, Help/About, installation selection and recovery.
- Port behavior and reusable components. Do not copy DiRT2 native offsets,
  content identifiers, executable patches or assumed XML semantics.

## Ordered implementation checklist

| # | Addition | Reference / implementation boundary | Status |
| --- | --- | --- | --- |
| 1 | Remember launcher size and maximized state; show version in the title | Adapt `WindowPreferences.vb`; DPI-aware fit to current work area. Store separately so closing never saves unsaved game settings. | Implemented in 0.1.6; automated UI checks passed |
| 2 | Initial output-scale control | Implemented in 0.1.6; replaced by the single GRID2VR-style control in step 3 at the user's request. | Superseded by step 3 |
| 3 | One headset-relative VR render-scale slider | Follow GRID2VR: 100% scene size equals the connected headset's OpenXR recommendation per eye; output remains fixed at that recommendation. Show detected and requested dimensions. Re-query before launch, bound allocations and restore game graphics afterward. No separate output slider. | 0.1.8: 100% live scene/output and 3D visuals confirmed; other scales/reset/stability remain open; 2D readability blocker moved to next priority |
| 4 | Desktop resolution, VSync, display mode and borderless | Validate DiRT3 XML fields, then adapt desktop-only `BorderlessWindow.vb`. Preserve separate VR settings and current native desktop defaults. | 0.1.17: 660 automated checks passed; Borderless and Windowed 1920×1080, Alt+Tab and normal quit confirmed by user. VSync On applied and retained in XML. Foreground Fullscreen launch reaches 1920×1080; returning from Alt+Tab switches to native 4K. User accepted leaving this as a known issue on 2026-10-29 |
| 5 | Named launcher setting profiles | Follow the reference panel interaction/layout; its current profile storage manages careers, so use an independent DiRT3 settings-only store. Versioned import/export, validation and unsaved-edit handling; no game save editing. | Implemented in 0.1.18; 735 launcher checks, rendered UI review, all 22 package files/ZIP and read-only installation check passed. No game launched. Details in docs/settings-profiles.md |
| 6 | Startup conveniences | Verify DiRT3 startup movie entries before adapting `StartupMovies.vb`. Evaluate background play/focus separately; no copied DiRT2 patches. | 0.1.19 logo skipping accepted in desktop and VDXR at 100%, with retained copyright/Press Start and tracked 3D menus. 827 launcher checks pass. Desktop recovery was automatic; VR's known graphics resolution/refresh rewrite required review, then all originals were restored. Background play remains deferred for native research. Details in docs/startup-conveniences.md |
| 7 | Keyboard and controller VR bindings | Configurable recenter, then VR presentation toggle; adapt `BindingCells.vb`, `Controllers.vb` and capture handling. Foreground-only actions, chord/repeat handling and disconnect tests. | Keyboard recenter key with Ctrl/Alt/Shift implemented after 0.1.20 (`launcher/RecenterShortcut.vb`, `src/recenter_shortcut.h`, DIRT3VR_RECENTER_KEY); controller/wheel buttons and the VR presentation toggle remain planned |
| 8 | Seat adjustment and per-car presets | Adapt `SeatSettings.vb`/`SeatPositions.vb`; establish DiRT3 car identity, then translation/rotation/reset and persistent defaults. Confirm axes and scale in headset. | Planned |
| 9 | Graphics options | Add verified native MSAA/anisotropy first, then bloom, depth of field, shadows, crowd/detail, mirrors and filters individually. Every displayed option must affect an implemented DiRT3 path. | Planned |
| 10 | Driving controls and binding wizard | Adapt controller detection, input monitor and `DrivingControlsForm.vb`; verify DiRT3 binding format before writing it. Preserve existing wheel/controller mappings. | Planned |
| 11 | Practice / Race launch selection | Establish DiRT3 event catalog and supported launch mechanism first. Then car/class, discipline, track/route, weather, opponents, difficulty and laps where applicable. Native career remains available. | Research gate |
| 12 | HUD placement and visibility controls | Establish separate DiRT3 HUD capture before exposing follow-view, distance and element visibility. Validate menus, cockpit/external views and replay transitions. | Capture, menu framing, intro handoff, HUD proportions and transitions accepted through 0.1.14; frontend shadows/stability accepted in 0.1.16. Placement controls remain planned |
| 13 | Replay library and additional camera tools | Verify DiRT3 replay storage/loading before library/import/export. Add chase free look and photo/free-camera behavior only with independent game-state and pause handling. All normal views already use stereo. | Research gate |
| 14 | Runtime diagnostics and recovery parity | Add actionable diagnostics and narrowly scoped API-layer handling where evidence warrants it. Resolve SteamVR orientation in an approved isolated test. Keep VDXR regression coverage throughout all batches. | Continuous / live gate |
| 15 | Delivery conveniences | Local installer, repair/uninstall and version/update UI after package ownership is established. Update feeds and publication require a real approved release destination and separate publishing authorization. | Planned |

The previous scope exclusions remain: bespoke LAN services, save/profile hacking,
imported cars/tracks and DiRT2-specific executable fixes. Native DiRT3 Steam
profile and multiplayer systems are preserved. Launcher setting profiles in
step 5 are independent of game saves.

## Completion rules for each batch

1. Implement only settings backed by working code; keep old defaults unless an
   explicit behavior change requires otherwise.
2. Verify settings migration/isolation, launch transport and relevant native
   guards. Exercise both appearance modes and narrow/wide layouts for new UI.
3. Preserve the current working package, produce a new local patch version and
   verify package contents. Do not publish automatically.
4. Record automated checks separately from live acceptance. Ask before every
   game launch; headset/runtime/effect claims need the corresponding live test.

## First batch acceptance

Window persistence must survive normal/maximized/minimized closes and DPI/work
area changes without saving unsaved launcher settings. Damaged window preferences
must not block startup or recovery. Headset output scale must survive settings
round trips/theme changes, reach only VR child processes, reject invalid values,
and produce the expected mock-runtime eye dimensions while retaining allocation
limits. No live launch is needed for these automated checks.

Version 0.1.6 passes all 15 native CTests and 494 launcher checks. Coverage includes
corrupt/missing window preferences, normal/maximized/minimized closes, logical
window dimensions at 100/150/200% DPI, old settings defaulting to 50%, settings
isolation and validation, process-only scale transport, both appearance modes,
narrow/wide layouts, unsaved edits, mock eye dimensions and allocation limits.
Rendered UI screenshots were checked at the host's 150% DPI. No game or real XR
session was started; full UI rendering at other DPIs and live scale acceptance
remain separate checks.

Verified portable build: `artifacts/packages/0.1.6-20261028-152247/DiRT3VR`.
All 22 payload files, native architectures and archive contents passed package
validation; the same 494 launcher checks passed with this package data.

## Revised step 3, 0.1.7

The user's single-slider correction replaces the original split-resolution plan.
Reference: GRID2VR commit `cc481c679ffa8ee0391291d001ad83349388b4ca`, specifically
`launcher/HeadsetResolution.cs`, `tools/Headset-Resolution.ps1` and the native
resolution validation. Referenced files were clean and only read.

- One 50–300% VR render slider, default 100%; no output-scale control.
- 100% means the runtime recommendation, not the headset panel's physical pixels.
- Refresh displays headset identity, output and requested scene dimensions, and
  the supported maximum. Runtime/path changes invalidate the displayed result.
- Every VR launch queries the selected x86 runtime again before staging files.
- Scene dimensions use integer floor(baseline × percentage / 100). Output stays
  at the full detected recommendation. No fallback to stale or fixed dimensions.
- DiRT3 currently permits 320×180 through 4096×4096 for both scene and output.
  XR output retains its 512 MiB aggregate color limit and accepts at most four
  images per eye in this mode. Unsupported requests are refused, not clamped.
- Old HeadsetScale values do not become scene scale. Existing unrelated settings
  remain; new VrRenderScale defaults to 100 without writing on load.
- Recovery validates only the owned graphics changes plus bounded width/height,
  and still restores journals from the older fixed-resolution profile.

All 15 native tests and 543 launcher checks pass. The rendered Graphics tab was
reviewed at 150% DPI, with synthetic detected dimensions. No game or live headset
session was started. Native checks refuse changed recommendations and mismatched
backbuffer dimensions. GRID2VR's scene-size enforcement hooks are game-specific
and were not copied: DiRT3 must still demonstrate that its configuration produces
and retains the requested scene size through menus, driving and transitions. If
it clamps dimensions, establish a guarded DiRT3 hook before completing this gate.

Verified package: `artifacts/packages/0.1.7-20261028-154618/DiRT3VR`.
All 22 payload files and archive contents passed validation; the 543 launcher
checks also pass against this package data. The later approved attempts are recorded below.

## Step 3 live result and 0.1.8 correction

The first 0.1.7 attempt exited before rendering because its Steam API could not
connect. The user then approved one Steam restart and one retry. After restart,
the x86 Steam helper connected and PID 23596 reached DXGI creation, but requested
3072x3264 became 1280x720. The mismatch guard stopped VR. The game was closed;
graphics/restrictions were restored exactly, proxy removed and journal cleared.
The game had also rewritten refresh rate, so semantic XML recovery required
explicit inspection; broad XML conflict protection remains in place.

0.1.8 adds DiRT3-specific guarded creation, resize and recreation handling based
on GRID2VR's mechanism. Six dimension words are transactional; unsupported calls,
foreign renderers and threads are refused. All instruction signatures match the
installed executable. All 16 native tests pass. See
`docs/research/scene-resolution.md` for evidence and limits. The slider is not
accepted yet: requested dimensions, VR appearance, menus, camera transitions,
memory use and reset behavior still require live testing. Ask before each launch.

### 0.1.8 attended result and immediate next steps

The user separately approved 0.1.8. Initial native sizing now works: 3072x3264
game backbuffer and both XR eyes. At least 11,640 stereo submissions were logged,
then orderly native XR shutdown on game exit. User: "VR in menus and driving
works great." The user also reported double vision, proximity and clipping in
the HUD and all non-world 2D menus, including before races. This is not full
interface acceptance. The game has exited and exact file restoration is verified.

Prioritize the native part of step 12 before further launcher controls:

1. Use DiRT3's own UI shader identities and runtime depth/target state to
   separate screen-space draws from real 3D frontend objects. Static mapping is
   implemented in `tools/research/inspect_ui_shaders.py`; runtime filtering added in 0.1.9.
2. Adapt DiRT2VR's first-eye HUD capture with scoped state restoration and
   second-eye suppression. Establish coverage for the whole 2D interface.
3. Submit the captured interface as one transparent OpenXR panel with a readable
   aspect ratio, bounded width and comfortable distance. Preserve genuine world
   stereo and all game cameras. Avoid adding settings without working behavior.
4. Test alpha/masks, state restoration, frame ownership and failed acquisitions
   offline, then ask for a fresh attended test covering pre-race menus, HUD,
   pause/replay and an external camera. Do not mark the interface fixed until it
   is visibly readable. Non-100% scale and reset acceptance also remain open.

See `docs/research/ui-interface.md`. No extra launch is authorized by the two
already-consumed approvals for 0.1.7 and 0.1.8.

### 0.1.9 HUD implementation

The user authorized continuation. Implemented DiRT3 shader tagging, scoped HUD
capture, second-eye suppression, transparent XR panel and desktop mirror
composition. Panel sizing and resource ownership are bounded; empty or failed
captures cannot submit stale UI. Placement/visibility controls remain deferred.
No game launch was performed during implementation. Next gate is an explicitly
approved VDXR test of pre-race menus, HUD, pause, tracking and an external camera.
If UI remains doubled/clipped, use captured/suppressed draw counts to establish
missing coverage or transform handling before expanding launcher controls.

Verified package: `artifacts/packages/0.1.9-20261028-180550/DiRT3VR`.
All 19 native, 543 launcher and 53 diagnostic/recovery tests pass, including
actual asset shader tagging/routing in a synthetic WARP process. All 22 files,
archive contents and architectures verified. The user approved and completed one 100% VDXR HUD test. That approval is consumed.

### 0.1.9 attended result and 0.1.10 follow-up

PID 7404 ran in VDXR at 3072x3264 per eye. At least 8,880 stereo submissions,
matching captured/suppressed UI counts and orderly XR shutdown were logged.
The user confirmed the racing HUD looks great and reported three remaining
issues: horizontally cropped menus, persistent floating black bars and short
doubled/frozen frames at most transitions. Screenshots and logs are retained in
`artifacts/diagnostics/hud-0.1.9-20261028`. Original game files are restored;
the same reviewed width/height/refresh rewrite needed manual recovery review.

0.1.10 corrects asymmetric temporal-history invalidation: both eye slots are
primed after camera/reference changes and gaps in completed desktop frames.
This is a confirmed source defect, not yet a confirmed fix for the observed
freeze. Bounded timing logs distinguish XR wait from engine eye-call time.
F8 records one requested frame's draw state, buffer prefixes and captured RGBA
panel. Obtain captures in a cropped menu and with the floating bars alone before
choosing an aspect or bar-removal rule; do not guess or disturb accepted HUD.
A fresh game-launch approval is required. No further launch has occurred.

Verified follow-up package: `artifacts/packages/0.1.10-20261028-183344/DiRT3VR`.
All 19 native, 543 packaged-launcher and 53 diagnostic/recovery checks pass.
Both 0.1.10 tests have ended. The user reported the same visual issues; the
restart (PID 32868) supplied three complete F8 captures. Original game files are
restored, with no proxy/pending journal/game process. See
`artifacts/diagnostics/hud-0.1.10-restart-20261028/after.json`.

0.1.11 implements the measured widescreen canvas correction and narrowly removes
the captured letterbox geometry. Physical HUD size is preserved while the panel
expands sideways. Tests and package verification precede a fresh approved headset
check. Transition freezes/doubling are still unresolved. Keep step 12 as the
current blocker before returning to scale acceptance and desktop graphics controls.


Verified 0.1.11 package: `artifacts/packages/0.1.11-20261028-190340/DiRT3VR`.
Native SHA-256: `4521b5bb801e6baa6a538e4a2ade9c2253c11ce27ff488f3ce38709192d03b43`.
Archive SHA-256: `3f5d218ce6500b0dc795b6ba697ef1d70dc4f0792e283f1b01e7e41b4416522b`
(46,563,185 bytes). All 22 files/archive verified; 20 native, 543 packaged-launcher
and 53 diagnostic/recovery checks pass. The user approved one VDXR 100% test.
PID 3664, session `cccffb86d6de4e2f8982c9ea3a0f77c6`, started at
2026-10-28T18:05:53Z. Logs confirm 16:9 canvas activation on the captured menu
camera. The test ended cleanly at 18:08:14Z. Approval consumed.
Evidence: `artifacts/diagnostics/hud-0.1.11-20261028`.

0.1.11 attended result: the user confirms all menu text is visible, floating bars
are gone, and the racing HUD remains readable in cockpit and external cameras.
Intro videos instead show a black rectangle; menu transitions still briefly
freeze with double vision. At least 9,720 stereo submissions and clean XR shutdown
were logged. Original graphics/restriction bytes, absent proxy, absent
`recovery/pending.json` and no game process are independently verified in
`artifacts/diagnostics/hud-0.1.11-20261028/after.json`.

Preserve 0.1.11 as the accepted menu/bar/HUD baseline. Next: trace the dedicated
movie draw path and the images reused during menu transitions; keep these issues
separate from the working interface canvas. Do not broaden shader routing or
claim a transition fix without evidence. Any further game launch needs approval.

0.1.12 is a verified diagnostic candidate, preserving 0.1.11 rendering while
automatically tracing movie, final-composite and outside-eye backbuffer draws.
The movie and transition defects remain open. All 21 native, 545 packaged-launcher
and 53 diagnostic/recovery checks pass; package/archive verification passes.
Package: `artifacts/packages/0.1.12-20261028-192518/DiRT3VR`. Await explicit approval
for one VDXR 100% run with intro videos and menu transitions, without F8. See
`docs/vr-prototype-status.md` for package hashes and diagnostic limits.

The 0.1.12 test is complete and its launch approval consumed. Both defects were
reproduced; automatic tracing identified late intro draws and the specific
screen-transition overlay. Original files are restored, with absent proxy and
absent `recovery/pending.json` verified in that run's `after.json`.

0.1.13 implements deferred submission for late movies and direct stereo cuts
in place of the captured-screen transition overlay. The menu/bar/HUD baseline
is unchanged; ordinary game-loading stalls may remain. All 22 native, 545
launcher and 53 diagnostic/recovery checks pass. The next gate is a freshly
approved VDXR headset check of intros, transitions, menu readability and the
racing HUD before returning to the launcher parity checklist.

Verified 0.1.13 package: `artifacts/packages/0.1.13-20261028-203129/DiRT3VR`.
All 22 payload/archive entries and architectures verified, with 545 checks
against the packaged launcher. A fresh launch approval is pending. Do not mark
the movie/transition blocker resolved until the user has tested this binary.

The user approved that single 0.1.13 test; PID 39560/session
`b01c9e4dc659475b86eb58b3aca641a4` is running. Late-video submission and stereo
resumption are logged, but visual results and final restoration remain pending.
Approval consumed; any additional launch requires a new request.

0.1.13 has now ended and original files are restored. User feedback accepts the
visible intro videos and says the doubled/frozen transitions seem gone. Startup
still leaves the legal splash early, and HUD instruments stretch in both mirror
and headset. Address these two items in 0.1.14 before resuming launcher parity;
do not undo the accepted transition change. The next game launch needs approval.

0.1.14 is verified at `artifacts/packages/0.1.14-20261028-210011/DiRT3VR`:
23 native, 545 launcher, 53 diagnostic/recovery and 545 packaged-launcher checks
pass, with all payload/archive files verified. It holds startup presentation
until the native legal screen closes and preserves mixed HUD/menu proportions.
Headset acceptance is pending; request fresh approval before the next game launch.

The approved 0.1.14 attempt exited with code 1 before loading the mod. The Steam
API probe cannot see/connect to the running Steam client. Exact prelaunch files
are restored and no proxy/pending recovery remains. Presentation acceptance is
still pending; Steam restart and another game launch require fresh approval.

The explicitly approved Steam restart and one 0.1.14 retry are complete. The user
accepted intro/splash completion, HUD proportions and transitions. Original files
are restored, with no proxy or pending journal. Both approvals are consumed.
The newly reported frontend triangle shadows moving with head tracking are being
addressed in 0.1.15 before continuing launcher parity. See
[frontend shadow evidence](research/frontend-shadows.md); the new binary requires
fresh launch approval and headset acceptance.

0.1.15 is packaged and verified at
`artifacts/packages/0.1.15-20261028-213512/DiRT3VR`: 26 native, 545 launcher,
53 recovery/diagnostic and 545 packaged-launcher checks pass. No game launch has
occurred. Await fresh approval for the frontend shadow check before proceeding
with the next launcher parity item.

The approved 0.1.15 run subsequently lost its graphics device during intro and
the game closed itself. No shadow acceptance was reached. Recovery is complete
and exact original files are restored. Isolated hardware rendering tests pass,
so the specific cause remains unproven. Launcher parity remains paused behind
this issue; the next proposed gate is a separately approved 0.1.14 control run.

The approved 0.1.14 control passed: user-confirmed stable menus/tracking and clean
XR shutdown. Files are restored. 0.1.16 is prepared with narrower shadow-pass
binding cleanup and device-removal diagnostics; automated/package/hardware
fixture checks pass. Await a fresh approved game test. The accepted baseline
remains 0.1.14; launcher parity work is still behind this shadow/stability gate.

The approved 0.1.16 attempt exited before loading the mod, with a failed Steam
API preflight (the orchestration should have stopped there). Files are fully
restored. No rendering validation occurred. A Steam restart and one retry need
fresh approval; the failed attempt's approval is consumed.

The separately approved Steam restart and 0.1.16 retry passed: the user confirmed
stability and fixed frontend triangle shadows, and explicitly accepted proceeding
without repeating the racing HUD check. The session shut down cleanly and exact
original files are restored, with no proxy or pending journal. 0.1.16 is the
working baseline. Resume the ordered plan at step 4 (desktop resolution, VSync,
display mode and borderless); placement controls remain later in the plan.
Both approvals are consumed. Any future game launch needs fresh approval.
