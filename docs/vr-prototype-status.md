# VR prototype status — 2026-10-28

Same-frame stereo replay and head tracking now work in the bounded Aspen
benchmark. The user confirmed correct depth and tracking with VDXR. The first
interactive cockpit tests failed: the classifier rejected `head-cam` with
`display-mismatch`, so VDXR presented only the virtual screen. The 0.1.4 log
identified the exact cause: owner far=2000 versus rendered far=1000, with all
other fields matching. Version 0.1.5 corrects that check and enables all valid
main 3D camera views as requested. The user now confirms that 0.1.5 works in VDXR;
its session log records at least 7,800 stereo submissions, including cockpit,
bonnet and chase views. Full camera/effect coverage and milestone 2 remain open.

SteamVR submitted stereo frames and shut down cleanly, but the user reported
an upside-down scene while also racing in GRID2VR. Visual acceptance failed.
Concurrent use is a possible confounder, not an established cause. Experimental
interactive launch is restricted to VDXR pending an isolated SteamVR retest.
The agent must ask and receive approval before every game launch.

## Implemented

- Exact executable and loaded instruction guards, x86 D3D11 forwarding,
  adapter/feature-level validation, MinHook and OpenXR integration.
- Reversible serial-render configuration, including the CPU restrictions that
  otherwise override `parallelUpdateRender=false`.
- Two eye renders of one prepared scene without another simulation update.
  Native temporal-history slots are selected and primed separately; owned
  camera fields and the selector are restored. Native list mutations survive.
- Six-degree-of-freedom camera transforms, asymmetric projections, expanded
  preparation visibility and isolated D3D11 texture presentation.
- Explicit stereo/screen frame ownership, bounded image allocation, tracking
  and session failure handling, and zero-layer aborts for incomplete frames.
- Interactive integration: stereo and tracking for all valid main 3D views,
  including external, reverse, replay and unnamed cameras; a virtual screen
  when there is no eligible 3D scene; foreground F10 recentering. Owner/name
  classification is diagnostic only. Displayed-camera matching remains exact.
- Owner-thread window-close/process-exit cleanup. Unsafe or foreign-thread
  exits log unavailable cleanup; GPU/XR objects are never destroyed from a
  foreign thread. Window-close cleanup completed in the failed cockpit session;
  separate normal Quit/Alt+F4 acceptance remains open.

The interactive integration is newer than the successful benchmark binary. Its
first two user tests reached OpenXR but failed cockpit stereo. The 0.1.5 test
passed the user's basic VDXR check. It remains an opt-in prototype with
`stereoVerified=false` until the broader acceptance gates are complete.

## HUD/menu implementation (0.1.9, partial live acceptance)

Portable package: `artifacts/packages/0.1.9-20261028-180550/DiRT3VR`.
Native SHA-256: `026a1ca5e7e752ba9fa745a0efa883bff0b121b6f4fe64b8d073aaa6c2769c54`.
Archive SHA-256: `602f3e3ad8df0866711705a0519c3add3a042a104ee790bc8f202b4f7dbcbb13`
(46,548,434 bytes). All 22 packaged files, archive contents and architectures
verified. All 19 native tests, 543 launcher checks (also against this package)
and 53 diagnostic/recovery tests pass. One separately approved VDXR launch
completed with at least 8,880 stereo submissions and clean XR shutdown.

Recognized 2D interface draws now have separate first-eye capture, second-eye
suppression and a shared transparent XR panel. Native 3D scene stereo remains
unchanged; original UI is composited back into the desktop mirror after the eye
copies. The panel uses the source aspect ratio, 2-metre distance and bounded
physical/image sizes. F9 recentres both scene reference and panel.

The user confirmed the racing HUD looks great, but reported cropped menu edges,
floating black bars with no menu visible and short doubled freezes at transitions.
This is partial interface acceptance. Original graphics/restriction bytes are
restored, proxy removed and recovery journal cleared. Evidence and screenshots:
`artifacts/diagnostics/hud-0.1.9-20261028`.

0.1.10 resets both eyes' temporal history and adds transition timings plus F8
captures. The user still reported all three issues. The first test (PID 42284,
session `5b35c26018294067a222e5f648c82575`) ended without captures. The requested
restart was performed by the user: PID 32868, session
`22b622d956414e15a6fda18088da4d29`, 17:40:33Z–17:41:21Z. Three complete F8
captures are retained under `artifacts/diagnostics/hud-0.1.10-restart-20261028`.
Original graphics/restriction hashes, absent proxy/journal and game exit were
verified in that directory's `after.json`. Both launch approvals are consumed.

Verified 0.1.10 package: `artifacts/packages/0.1.10-20261028-183344/DiRT3VR`.
Native SHA-256: `39a8a33ee1d103650272444b30a7808633f5d63b4b8c026a8c3d092d6e5cc0e6`.
Archive SHA-256: `9d7ac546b5ea26ae33ee8a302857f9d625bd451f37bc9e36e8f5c09d10c5fa68`
(46,559,022 bytes). All 22 files/archive verified; 19 native, 543 packaged-launcher
and 53 diagnostic/recovery checks passed before those tests.

### 0.1.11 captured-menu correction

The captures show a native widescreen orthographic UI projected with the tall
3072/3264 eye aspect. The new guarded post-VS correction restores the horizontal
canvas before clipping, using the exact DiRT 3 camera and three observed shader
signatures. The panel expands horizontally while retaining the physical size of
already-visible HUD elements. A separate exact shader/colour/geometry check
removes the observed overscan letterbox triangles. No generic black-pixel removal
or gameplay-camera assumption is used. CPU upload shadows avoid normal-frame GPU
readback; deferred command execution invalidates old shadows.

Headset acceptance is pending. Check complete menus, bars-only views, racing HUD
and an external camera. Transition freezing/doubling remains unresolved; do not
attribute a visual fix to the 0.1.10 history change. Ask before every new launch.
See `docs/research/ui-interface.md` for captured evidence and guards.


Verified 0.1.11 package: `artifacts/packages/0.1.11-20261028-190340/DiRT3VR`.
Native SHA-256: `4521b5bb801e6baa6a538e4a2ade9c2253c11ce27ff488f3ce38709192d03b43`.
Archive SHA-256: `3f5d218ce6500b0dc795b6ba697ef1d70dc4f0792e283f1b01e7e41b4416522b`
(46,563,185 bytes). All 22 files/archive verified; 20 native, 543 packaged-launcher
and 53 diagnostic/recovery checks pass. The user approved one VDXR 100% test.
PID 3664, session `cccffb86d6de4e2f8982c9ea3a0f77c6`, started at
2026-10-28T18:05:53Z. Logs confirm 16:9 canvas activation on the captured menu
camera. The test ended cleanly at 18:08:14Z. Approval consumed.
Evidence: `artifacts/diagnostics/hud-0.1.11-20261028`.


## Resolution correction (0.1.8)

Portable launcher: `artifacts/packages/0.1.8-20261028-172129/DiRT3VR/DiRT3VR.exe`.
All 22 payload files, archive contents and architectures verified. Native SHA-256:
`cb8b755598c7f0cd5db776699dcd0c88bcc65cafe4b5cc2c9212277a5baca39f`.
Archive SHA-256: `1e39d9c53266c49405480b5d43618f363c7a77856af33cf336b54724c4bcc6a6`
(46,539,845 bytes). All 16 native tests, 543 launcher checks against this package
and 53 diagnostic/recovery tests pass.

The approved Steam restart repaired the client/API connection. The one approved
retry reached rendering but clamped 3072x3264 to 1280x720; VR stopped on the
resolution guard. See that result
below and `docs/research/scene-resolution.md`.

0.1.8 adds guarded native scene sizing for initial creation, resize and
recreation. All 16 native tests pass, including on-disk instruction verification
and synthetic rollback/ownership failures. An additional user-approved launch
now proves initial creation at 3072x3264 and matching XR output (details below).
The user confirms VR menus/driving work, but the 2D interface is unreadable in
places. Exact file restoration is verified. HUD/menu separation is the next
priority; other scales, reset and sustained stability acceptance remain open.
Recovery still preserves semantic XML conflicts for review; its policy was not
broadened to discard game or user edits. User-confirmed 0.1.5 remains available.

## Previous package (0.1.7)

Portable launcher:
`artifacts/packages/0.1.7-20261028-154618/DiRT3VR/DiRT3VR.exe`.
The ZIP is 46,537,200 bytes, SHA-256
`5db27b272541c9765943f588f9379aa1fde6a5285b4fd235a74961445041956d`.
Native proxy SHA-256:
`fb81a4ca60aeb48e290177b81748fc79c555e92f652e9d6b7ee07a35aa22f5d5`.
All 22 payload files, ZIP contents and binary architectures passed validation.
All 15 native CTests and 543 launcher checks pass, including the actual package
capability data. UI screenshots with synthetic headset dimensions were reviewed.
No game or live headset session was started during package validation; subsequent
approved attempts are recorded below.

The user revised step 3 to follow GRID2VR's one-slider model. VrRenderScale now
runs from 50 to 300%, default 100%, relative to the selected runtime's recommended
per-eye dimensions. Output remains fixed at that recommendation. The UI displays
the detected headset and calculated scene/output sizes; stale preview dimensions
are discarded when the runtime or manifest changes. Launch re-queries before
staging and refuses unsupported dimensions. Legacy HeadsetScale values are not
reinterpreted as scene scale; unrelated preferences remain intact.

Scene configuration is reversible, and recovery verifies the exact owned fields
with bounded variable width/height. Native initialization verifies the recommendation
still matches the query and that the backbuffer matches the requested scene size.
Interactive limits are 4096 per dimension, up to four XR images per eye, and the
existing aggregate 512 MiB XR color budget. The older bounded benchmark path
retains its half-size output and existing 8 Mi-pixel allocation limit.

Live acceptance remains pending: confirm DiRT3 accepts and retains the requested
scene resolution, then inspect menus, driving and camera transitions. GRID2's
native scene-size enforcement hooks were not copied; if DiRT3 clamps the request,
a game-specific guarded enforcement hook is required (implemented in 0.1.8). Do not treat automated
checks or this package as a new headset pass. Ask before every game launch.

## Previous package (0.1.6)

Portable launcher:
`artifacts/packages/0.1.6-20261028-152247/DiRT3VR/DiRT3VR.exe`.
The ZIP is 46,528,757 bytes, SHA-256
`6a140ccbcf5e34511ed039ec5a74f636372bcfec0b1472d9a7ee273a13f06fae`.
Native proxy SHA-256:
`3a73fb248e0cad253389bea030b7dbf0fecf4548e59daac05c9ffdbcc2972aec`.
All 22 package files, ZIP contents and binary architectures passed validation.
All 15 native CTests and 494 launcher checks passed, including checks with the
actual package capability data. No game or live XR session was started.

The first launcher parity batch adds DPI-aware window-size/maximized persistence,
a build version in the title, and a 25-100% headset output slider. The previous
50% default is retained. Settings travel only to the VR process; native parsing
and the existing texture/allocation limits remain enforced. Window persistence
is independent of unsaved game settings. Scene rendering remains 1280x720, so
output scale alone does not add scene detail. Mock-runtime tests exercise scaled
eye dimensions and allocation limits; these do not establish headset acceptance
for this new binary or nondefault scales. The all-camera stereo path is unchanged.

See `docs/launcher-parity-plan.md` for the ordered remaining work. Independent
scene resolution is next. The user-confirmed 0.1.5 package remains available.

## User-confirmed baseline (0.1.5)

Portable launcher:
`artifacts/packages/0.1.5-20261028-142136/DiRT3VR/DiRT3VR.exe`.
The ZIP is 46,525,275 bytes, SHA-256
`d94584504746dfa89aa75d0cc245e9ec0fcf2c07e3d574fb2b0b0e07b6f47456`.
Native proxy SHA-256:
`94c4986b14c8ad024c6d011a3dc31808f0642080ff549613fed7be4f5cc4000e`.
All 22 package files, ZIP contents and binary architectures passed validation.
All 15 native CTests and 426 launcher checks passed, including the actual
package's capability data. Regressions reproduce the observed source far=2000
versus displayed far=1000, enable every named/unnamed valid main scene and retain
refusal for invalid or mismatched displayed cameras. Camera transitions prime
the second eye's history. The agent did not launch a game for this change.
The user subsequently launched 0.1.5 and reported "Works." The recovery journal
confirms the proxy hash above. PID 37420 submitted at least 7,800 stereo frames;
all logged pair samples report camera restoration and equal per-eye history
serials. The log includes eligible bonnet, head-cam, chase_close, chase_far,
cinematic and unnamed views. Window-close XR shutdown completed, followed by
exit 0 and a launcher receipt reporting restored original files.

Evidence is retained in `artifacts/diagnostics/interactive-20261028-pid37420`
(native log, session metadata and final launch receipt). Native log SHA-256:
`8d969cf7a7c5eaf89e8c0e77d494bb8fe59554e7a07790152ca4c4e97ff3eada`.
This is a basic VDXR success, not evidence of every camera/effect or sustained
driving acceptance. No published package or capability manifest was changed.

## Previous diagnostic package (0.1.4)

Portable launcher:
`artifacts/packages/0.1.4-20261028-132715/DiRT3VR/DiRT3VR.exe`.
The adjacent ZIP is 46,525,110 bytes, SHA-256
`0d4d3808ebb82e40039b9a5e5323894db8e254fe788514539626174fbddb6cdc`.
All 22 packaged files, ZIP contents and binary architectures passed validation.
The native proxy SHA-256 is
`2c921f87349ea28c972a458ab4a0fa1fcfa979deef24268a67e54bdb712c3448`.
All 15 native CTests and 426 launcher checks passed, including the actual new
package's capability data. No game or live headset session was launched.

Camera mismatches retain every differing compared float as offset, exact bits
and value. Persistent Head mismatches are sampled at most once per second, with
a maximum of 12 detail lines for the process. Tests cover all 15 compared fields,
the sampling interval, exhaustion and unchanged camera refusal. The existing
guard is not bypassed; this diagnostic package does not claim to fix matching.

The failed session's recovery receipt reports exit 0. A read-only check confirmed
no running game, no installed `d3d11.dll`, and both the graphics configuration and
restrictions files matching their original recorded hashes.

## Previous local package

Portable launcher:
`artifacts/packages/0.1.3-20261028-124652/DiRT3VR/DiRT3VR.exe`.
The adjacent ZIP is 46,523,747 bytes, SHA-256
`3a395e046a0ead19e5c0e3ddd01e67d6283cb3e145d7bf276d35ea06bdfcc43e`.
All 22 packaged files, ZIP contents, x64 launcher and x86 native binaries passed
validation. The unchanged native proxy SHA-256 is:
`f2cb7f137c335613897b6909129d1bf5ab888a21fdc05306cbb15f9a3dc0d782`.

Version 0.1.3 fixes the reported `0xEF` error: Windows PowerShell 5.1 had emitted
a UTF-8 BOM in 0.1.2's capability JSON, while the launcher's byte deserializer
rejected it. The regression reproduced the exact error before the fix. The
reader now accepts one leading UTF-8 BOM without rewriting the file; malformed
or misplaced prefixes remain invalid. Package JSON is explicitly BOM-free.
All 424 launcher checks passed against both the old BOM-bearing capability file
and the new package, including the actual opt-in control enabling Launch VR in
an offscreen form. No game or live runtime was launched for this correction.

That package was delivered with no diagnostic proxy in the game folder and the
original restrictions hash verified. The user then launched the failed cockpit
session below. The agent has not launched a game since the instruction to ask
first; every further game launch requires explicit approval.

## Live evidence

Supported executable SHA-256:
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.

| Folder under `artifacts/diagnostics` | Result |
| --- | --- |
| `20261028-104835-08735836` | 120 consecutive same-frame pairs, sequences 792–911. Camera restoration, history and Present ownership passed. Six sampled eye images show spatial parallax. Benchmark exited 0, owned files restored, protected originals unchanged. |
| `20261028-105613-2e8bcca4` | VDXR: at least 2,319 stereo submissions, approximately 90 fps during sampled stereo, 1536×1632 per eye. User confirmed correct depth and tracking. Explicit XR shutdown, benchmark exit and recovery passed. |
| `20261028-110012-92ab6f8f` | SteamVR: at least 2,218 stereo submissions, approximately 90 fps during sampled stereo. Shutdown and recovery passed. User reported upside-down presentation during concurrent GRID2VR use; visual acceptance failed. |
| `interactive-20261028-pid33348` | User-launched VDXR session: OpenXR initialized, at least 7,800 screen frames, no stereo submissions. User confirmed a race in cockpit view. `head-cam` failed the display match. Native window-close shutdown completed; launcher reported exit 0 and restored owned files. |
| `interactive-20261028-pid31784` | User-launched 0.1.4 VDXR session: at least 7,500 screen frames, no stereo submissions. Two Head samples isolate owner far=2000 versus selected/global/main far=1000; all other compared fields match. Chase views also reached the old external identity, which was deliberately screen-only. Shutdown and recovery completed with exit 0. |
| `interactive-20261028-pid37420` | User-launched 0.1.5 VDXR session: at least 7,800 stereo submissions, sampled pair restoration/history checks passed. Cockpit, bonnet, chase, cinematic and unnamed views recorded as eligible. User confirmed "Works." XR shutdown, exit 0 and launcher recovery receipt completed. |

Both benchmark headset runs used proxy SHA-256
`46caf7e9ab20d49c542fe37d961e0940c1ad47cb4302174a83d7e48e3aba06fc`.
Submission counters are sampled lower bounds. Benchmark chase cameras do not
establish interactive cockpit, HUD or effects acceptance.

The failed interactive run used proxy SHA-256
`f2cb7f137c335613897b6909129d1bf5ab888a21fdc05306cbb15f9a3dc0d782`.
Its preserved native log has SHA-256
`73cee7bd407729d67fda7330450f4d13f137d5791feb77b4b00e5af932ce636c`.
The old log reports only the match failure, not the differing camera fields.
Version 0.1.4 retains exact acceptance while adding bounded comparisons through
the camera-copy chain, including settled cockpit samples. Launcher status now
distinguishes virtual-screen output from stereo submissions.

Earlier unsuccessful runs remain intact. The unsupported 10-bit screenshot
format was fixed, and an elapsed-time replay trigger replaced a fixed sequence
that was never reached with a low desktop frame rate. Some early runs regenerated
native desktop-resolution information; that game output was preserved and those
overall results were not rewritten as successful recovery.

## Validation and remaining work

Fifteen native CTests passed again after the all-camera activation fix. They
cover forwarding/refusal, WARP presentation state, camera identity/restoration,
pose/projection math, stereo/screen ownership, partial-eye failure, nested
presentation, synthetic close/exit forwarding, native-stereo refusal, foreground
near-plane restoration, installed camera guards, probe errors and exports.
Runtime tests use a synthetic OpenXR implementation, without a headset session.
Fifty-three diagnostic-runner Python tests passed before this diagnostic change.
All 426 launcher checks passed after the status update, including screen/stereo
transitions and offscreen UI. A clean interactive recovery was observed in the
failed cockpit session; UAC cancellation and interrupted-session recovery still
need attended acceptance.

Further approved live tests should cover bumper, reverse and replay, F10,
extended driving and effect correctness. Version 0.1.5
uses the actual displayed 3D camera for activation; it does not require a
live-driving or cockpit label. Pause/replay/flashback transitions still need
visual acceptance. Normal Quit, Alt+F4,
runtime restart, repeated launches, UAC and interrupted-install recovery need
attended acceptance. SteamVR requires an isolated orientation test; bounded
logs now record runtime pose, recenter reference, relative eye/FOV and native
camera basis to localize any inversion.

Separate HUD capture, seat adjustment, effect validation and sustained driving
across cars/weather remain open. Both runtimes must pass before milestone 2 is
complete. No game or live runtime is launched by automated native tests. No
release was published, and DiRT2VR remains a read-only source reference.

### Approved 0.1.7 test: startup blocked

The user approved one launch on 2026-10-28. VDXR and the launcher detected Meta
Quest 3 at 3072x3264 per eye; 100% requested matching scene/output dimensions.
PID 32492 exited with code 1 in about 102 ms, before any renderer log. Resolution
acceptance remains untested. Original graphics and restriction hashes were
restored, the proxy removed and the recovery journal cleared.

A standalone x86 helper using this game's Steam API returned false for both
client-running and initialization checks despite a visible Steam process; its
registered client PID was absent. A Steam connection problem is the current
lead, not a proven renderer failure. No Steam restart or second game launch was
performed at that point. Evidence: `artifacts/diagnostics/resolution-0.1.7-20261028`.

### Approved Steam restart and one retry: resolution clamp confirmed

The user then authorized a Steam restart and one game retry. Steam PID 35264
replaced 23176 and its client registration matched; the x86 helper reported both
client-running and API initialization successful. DiRT3 PID 23596 started at
2026-10-28T15:58:07.8810977Z through launcher 0.1.7, with 100% requesting
3072x3264. Its log reports actual 1280x720 and `scene-resolution-mismatch` before
XR initialization. This is a failed scene-size test, not a headset pass.

The owned game was closed through Stop VR / quit. Its XML differed from the
applied file only in width, height and refresh rate (3072x3264 -> 1280x720,
120 -> 100). Canonical comparison preserved all other content. After inspection,
the exact original graphics bytes were restored and Restore original files
completed recovery. Original graphics hash:
`04e438e48c74528f7b618968c3db4f12ade1022ef35c93ef2adfa8ba64a6f082`;
restriction hash:
`45257db5fa64d833575d569ec6f3b03344b5f40be25830960a8d11783276470b`.
Proxy and pending journal are absent, with no game process running. Exit code
was not reliably captured. Native/session/XML evidence and a fresh cleanup
receipt are in `artifacts/diagnostics/resolution-0.1.7-20261028/retry`.

### Approved 0.1.8 test: scene sizing confirmed; 2D interface blocker

After a fresh explicit approval, PID 32448 started at
2026-10-28T16:24:05.2353452Z with launcher PID 42384, package
`0.1.8-20261028-172129`. The verified renderer began at 1280x720; the hook reports
`guarded=1` and DXGI reports a 3072x3264 backbuffer. Both VDXR eyes are 3072x3264,
three images each. At least 2,280 stereo submissions have matching eye history,
camera restoration and projection updates. No runtime stop or size mismatch
appears in that sample. At 16:24:52Z, process private memory was 848,150,528 bytes
and peak virtual memory 1,558,687,744 bytes; this is a startup sample, not a
sustained-driving memory acceptance.

The user reported "VR in menus and driving works great" but the HUD and all
non-world 2D menus were doubled, too close or clipped. An external-camera pass
was not separately confirmed. The runtime reached at least 11,640 stereo
submissions and logged `game-process-exit` followed by shutdown complete at
16:26:45.877Z. The game had already exited before the assistant's close attempt;
no close action was sent and no additional launch occurred. Exit code is unknown.

The same three-field hardware XML rewrite required review before restoration.
All remaining XML nodes, attributes, comments and processing instructions were
compared. Exact original graphics/restriction hashes are restored; proxy and
pending journal are absent. `after.json` verifies cleanup at 16:31:34.9907008Z.
Evidence is in `artifacts/diagnostics/resolution-0.1.8-20261028`; session directory
is `session-579b6ebb07cb4582aa4cb543e90de1c9`. This is scene-size and user-confirmed
3D evidence, not full UI, runtime-parity, reset or sustained-driving acceptance.

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

### 0.1.12 movie and transition investigation

Verified diagnostic package: `artifacts/packages/0.1.12-20261028-192518/DiRT3VR`.
Rendering remains unchanged from the accepted 0.1.11 HUD/menu baseline. Diagnostic
logging now records bounded movie and backbuffer draw metadata automatically for
up to 180 seconds (512 records), without F8 presses or GPU readbacks. Small
synchronous log writes mean this trace is for draw-path diagnosis, not timing
acceptance. The black movies and doubled transitions are not fixed yet.

All 21 native, 545 packaged-launcher and 53 diagnostic/recovery checks pass.
All 22 package files, archive contents and architectures are verified. Native
SHA-256: `caa1b65bedf2dded9b0afbe16e09c25a1295be74d995a42a3c0530784f89f159`.
Archive SHA-256: `14344e7b37459e2542b13c9a6a7947be073003e0252260936a92e6d4f2231bb6`
(46,571,077 bytes). Fresh game-launch approval has been requested; no 0.1.12 game
launch or headset test has occurred. Evidence will be retained in
`artifacts/diagnostics/presentation-0.1.12-20261028`.

The user subsequently approved and completed that single run: PID 32504,
session `a3e0ada6a33e4761b3a4d291ae739539`, 19:13:22Z to 19:16:15Z. Both defects
were reproduced. The trace contains 512 records, including the late YUV movie
draw and both screen-transition variants. At least 12,960 stereo submissions
and clean XR shutdown are logged. This run's game rewrite changed width, height
and refresh rate only; reviewed restoration recovered the exact original bytes.
`after.json` confirms no proxy, no `recovery/pending.json`, and no game process.
The 0.1.12 approval is consumed.

### 0.1.13 candidate

Interactive stereo frames now remain pending until Present. Recognized late
intro video draws replace the complete pair with a current shared 16:9 panel.
The exact captured screen-transition pass is bypassed within stereo eyes,
producing a direct cut instead of blending the same captured view into both
eyes. Native loading pauses may remain. The accepted 0.1.11 canvas/HUD treatment
is retained. See `docs/research/ui-interface.md` and the 0.1.12 `findings.json`.

All 22 native, 545 launcher and 53 diagnostic/recovery checks pass. Tests cover
late movie ownership, moved/pending/abandoned frames, incomplete eye rejection,
stale overlay rejection, shader-state guards and preserved resource bindings.
The candidate has not been launched or visually accepted. A new approval is
required before testing 0.1.13 in the game.

Verified candidate: `artifacts/packages/0.1.13-20261028-203129/DiRT3VR`.
Native SHA-256: `8913bfeb498b7971947891cf5a4794fc3aac89027f7ab013a14535700853e29f`.
Archive SHA-256: `ef5fd9a8c563a462674d9640d4b79f375a8a91835abecba4978cd6cd2c0b0a8e`
(46,576,260 bytes). All 22 payload files, archive and architectures verified;
545 checks also pass against this exact packaged launcher. Approval for one
VDXR 100% run has been requested, with intro playback, menu transitions and HUD
regression checks. No 0.1.13 game launch has occurred yet.

The user then approved one 0.1.13 VDXR 100% launch. PID 39560 started at
2026-10-28T19:35:22.8583898Z through launcher PID 29832; session
`b01c9e4dc659475b86eb58b3aca641a4`. The installed proxy hash matches the verified
candidate. Logs show `source=late-intro-video` submissions and subsequent stereo
resumption. The test is running; user visual acceptance and final recovery are
pending. This approval is consumed and does not authorize a restart.

The 0.1.13 test ended at 19:37:14Z with clean XR shutdown. The user confirms that
intro videos display correctly and the doubled/frozen transitions seem gone.
Two follow-ups remain: startup leaves screen mode before the final legal splash
has finished, and racing HUD instruments are horizontally stretched in both
the headset and mirror. Both supplied screenshots are retained with the run.
`after.json` verifies exact original graphics/restrictions, no proxy, no recovery
journal and no game process. Recovery launcher PID 15392 was opened only to
restore files; it did not start another game.

0.1.14 is being prepared to retain startup screen mode through the game's exact
ScreenInitialLegal exit and fit native HUD draws into the same canvas as corrected
menu draws. No further game launch is authorized. Preserve the accepted transition
behavior and use fresh approval for the eventual candidate test.

0.1.14 is built and packaged at
`artifacts/packages/0.1.14-20261028-210011/DiRT3VR`.
All 23 native, 545 launcher and 53 diagnostic/recovery checks pass, along with
545 checks against the packaged launcher. All 22 payload files, archive and
architectures verify. Native SHA-256:
`f6ea109251c59a7f6fa34b4d0cdb7a58ec7ab817f0c9d772f31f19f6d3bc4469`.
Archive SHA-256:
`5cc721c279a146306879d9d35db9eb2f598ffe5c6b98659767d0155baef9f101`
(46,578,313 bytes). Receipt:
`artifacts/diagnostics/presentation-0.1.14-20261028/package-verification.json`.
No game launch has occurred. Next: request one VDXR 100% launch to check the
complete copyright-screen handoff and HUD proportions in headset and mirror,
with the previously accepted menus/transitions retained. No F8 capture needed.

The user approved one 0.1.14 launch. Launcher PID 51648 started game PID 29324
at 2026-10-28T20:04:23.444355Z, session `8ed20d613ae542fcb152bc3114c56a14`.
The process exited with code 1 in about 100ms, before a game window or native
log appeared; no headset validation occurred. The separate existing Steam API
probe returned client_running=0 and initialized=0 while steam.exe PID 11148
was running. No automatic retry or Steam restart was performed.

The launcher restored this run's exact current graphics baseline (SHA-256
`d00a44ef16897b48b518b8837f390585e9347cef792e8429969f464212830f54`),
which superseded the prior run's desktop configuration, and original restrictions.
No proxy, recovery/pending.json or game process remains; see this run's after.json.
Approval is consumed. Ask for permission to restart Steam and retry once.

The user approved restarting Steam and one retry. Steam PID 11148 closed
gracefully, replacement PID 45252 started, and the API probe then reported
client_running=1 and initialized=1. Launcher PID 51648 started game PID 28880
at 2026-10-28T20:10:01.8093883Z, session `bbca47bbe24b45d29377f5e3a2f9d419`.
The installed proxy hash matches 0.1.14. Native intro hooks attached and the
log shows intro_screen=1 across the one-frame gap between movie clips. The
attended test ended with clean XR shutdown at 20:12:37.048Z. The game changed its
resolution/refresh and XML formatting during the run; the recovery review checked
the exact original/applied hashes and the canonical XML differences before
restoring the current original. At 20:13:50.6358775Z the proxy and pending journal
were absent and both original file hashes matched. Evidence is under this
version's `retry-1` directory. Retry approval is consumed.

The user confirmed all intro movies/splashes finish before 3D menus, racing HUD
proportions are correct in headset and mirror, and transitions remain clear.
They reported one new defect: the yellow frontend triangles' ground shadows
move with the headset viewpoint. The annotated screenshot is retained as
`retry-1/frontend-shadow-user.png`. The 0.1.15 correction and evidence are described
in [frontend shadows](research/frontend-shadows.md). No further launch is authorized.

## 0.1.15 prepared — frontend shadow mapping

Package: `artifacts/packages/0.1.15-20261028-213512/DiRT3VR`.
Native SHA-256: `26e0fa306361cb502f654047e7bd907ba9e23383784b5b2f96ce53dd629a5998`.
Archive SHA-256: `89fa5f0823ccc9b52361791854297da6501a042574168dcbe65c4c7052af8756`
(46,585,588 bytes). All 22 payload/archive entries and native architectures verified.
26 native tests, 545 launcher checks, 53 recovery/diagnostic tests and 545 exact
packaged-launcher checks passed. Receipt:
`artifacts/diagnostics/frontend-shadows-0.1.15-20261028/package-verification.json`.
No game launch or deployment occurred. The original file hashes still match,
with no game process, proxy or recovery journal. Launcher 34776 remains the old
0.1.14 instance. Use the new package for the next approved test.

The next gate is a single VDXR 100% headset test: rotate and lean while watching
the yellow triangles' ground shadows, then check racing shadows/HUD and retained
intro/transition behavior. Guarded-draw logs alone are not visual acceptance.

## 0.1.15 approved test failed — graphics device loss

The user approved one VDXR 100% launch. Steam API checks succeeded before it.
Launcher PID 36248 invoked Launch VR once at 2026-10-28T21:10:14.2344666Z;
game PID 31424 started at 21:10:14.8733188Z, session
`04c3771b3fe9481695ccd7cb7de2377d`. The deployed proxy matched the package hash.
The initial hidden-launcher window lookup did not invoke a game launch; the
later invocation above was the only use of this approval.

The frontend observer captured its native matrix and both eyes reached the
guarded draw path. During intro playback, NVIDIA event 153 at 21:10:35.9106276Z
reported an error on GPUID 100. Frame 139 failed; XR shut down at 21:10:36.375Z.
Subsequent Present and ResizeBuffers returned `0x887a0005`
(`DXGI_ERROR_DEVICE_REMOVED`). The user confirms the game closed by itself.
No successful shadow visual check occurred. The exact cause of device removal
is not yet established; do not attribute it to a specific API call or driver bug.

The recovery conflict was the same game-authored width/height/refresh rewrite as
the preceding run: 3072x3264/120 to 1280x720/100, plus XML formatting. Canonical
comparison confirmed no other semantic changes. Both versions were saved,
the exact current original was restored, and the launcher completed recovery.
At 21:13:41.4384087Z no game, proxy or pending journal remained and both original
hashes matched. Recovery launcher PID 48680 remains open. Evidence:
`artifacts/diagnostics/frontend-shadows-0.1.15-20261028/after.json`, native log,
driver-event.xml, recovery snapshots and the user's screenshot.

The standalone shadow test now also renders 600 small real GPU draws and checks
the resulting pixels. All 26 native tests pass; an explicit hardware/debug-layer
run also passes without D3D11 validation errors or device removal. This does not
reproduce the game's failed run or prove the feature is safe in-game. Runtime
code and the packaged 0.1.15 binary are unchanged by these test additions.

Next diagnostic gate: request a single control launch of the previously accepted
0.1.14 package in VDXR at 100%, before changing the runtime speculatively. No
further game launch is authorized; the 0.1.15 approval is consumed.

## Stable 0.1.14 control and 0.1.16 revision

The user separately approved one 0.1.14 control launch in VDXR at 100%.
Launcher PID 47228 started game PID 47160 at 2026-10-28T21:21:01.7321104Z,
session `3c22832220a944a3948cfa6586697457`. The exact accepted proxy was deployed.
The user confirmed stable 3D menus and tracking. Native logs reached 2,522 stereo
submissions with no device-loss event, then normal window close at 21:22:30.914Z
and XR shutdown at 21:22:30.967Z. This implicates the new shadow path but does
not identify which operation caused 0.1.15's device loss.

After the same verified resolution/refresh XML rewrite, exact originals were
restored and the launcher's recovery-only command completed the transaction.
At 21:24:45.7905751Z no game, proxy or pending journal remained. Evidence:
`artifacts/diagnostics/frontend-shadows-0.1.15-20261028/control-0.1.14`.
The control approval is consumed.

0.1.16 changes only the shadow pass's private-state cleanup from broad
`ClearState` to explicit CS binding release, matching GRID2VR's approach, and
adds `GetDeviceRemovedReason` logging on failure. The latter will distinguish
device-hung/reset/invalid-call failures if another run fails. The exact cause
is still a hypothesis; this is not a confirmed crash fix.

Package: `artifacts/packages/0.1.16-20261028-222409/DiRT3VR`.
Native SHA-256: `a50f3b7f3af99cd4e16250e6d78691f5c4793383c7bab166f36e59d644ff669b`.
Archive SHA-256: `a60de09381565e57487ea32079624157be5373a2f73c16d65b31b790f187097c`
(46,585,894 bytes), with all 22 entries verified. 26 native tests, 545 launcher,
53 recovery/diagnostic and 545 packaged-launcher checks pass; the standalone
hardware/debug-layer shadow test also passes 600 real draws. Fresh approval and
game/headset validation are required. 0.1.14 remains the accepted baseline.

The user approved one 0.1.16 VDXR 100% launch. The Steam API probe reported
client_running=0 and initialized=0, but the orchestration incorrectly continued
instead of gating on its result. Launcher 38192 invoked the game once; PID 28636
started at 2026-10-28T21:39:23.3387943Z and exited code 1 about 96ms later, before
a native log or game window. Session: `c40f27f6aeb84242b0af40ba21763645`. This does
not test the revised shadow rendering. The erroneous copied preflight=true
receipt field has been corrected with an explicit audit note, and the launch
script now stops on a failed API probe before invocation.

Automatic recovery completed and exact original hashes match, with no game,
proxy or pending journal. Evidence is in
`artifacts/diagnostics/frontend-shadows-0.1.16-20261028`. The approval is consumed.
Steam PID 32792 is present but inaccessible to the API probe; ask separately
before restarting Steam and attempting one retry. No further launch is authorized.

The user then approved a graceful Steam restart and one retry. Steam PID 32792
closed and PID 51172 started at 21:41:17.2766952Z. Both the orchestration and the
launch script obtained successful client-running/API-initialization probes
before invocation. Launcher 38192 started game PID 51828 at
2026-10-28T21:41:34.7887374Z, session `28e49f59dbdb4378a7613e05b729f6b4`.
The deployed hash matches verified 0.1.16. It completed startup presentation and
reached stereo menus, passing the earlier failure point without device errors
so far. User shadow/HUD feedback and final recovery are pending. Evidence is in
this version's `retry-1` directory. Restart/retry approval is consumed.

### 0.1.16 accepted result

The user confirmed the session is stable and the yellow triangle shadows are
fixed and correct under head movement. They did not repeat the racing HUD check
and explicitly accepted moving on with its prior validation. The log reached
5,081 stereo submissions with camera restoration, then game-process exit and
clean XR shutdown at 2026-10-28T21:43:13.870Z. No device-removal error was recorded.
This passes the attended frontend shadow/stability check; it does not establish
long-duration stability, SteamVR compatibility or fresh racing-HUD acceptance.

The same game-authored resolution/refresh XML rewrite was verified against the
exact applied snapshot, preserved, and restored to the exact prelaunch original.
At 21:44:45.8049872Z the game, owned proxy and pending journal were absent, and
both original configuration hashes matched. Receipt: `retry-1/after.json`.
The verified 0.1.16 package is the current working VDXR baseline. Resume launcher
parity at desktop display controls (step 4). No additional game launch is authorized.
