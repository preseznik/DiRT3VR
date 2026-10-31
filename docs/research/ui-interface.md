# 2D interface after the 0.1.8 resolution test

The user confirmed VR menus and driving but reported all non-world 2D elements
as doubled, too close or off-screen, including HUD and pre-race menus. In 0.1.8,
`game_vr.cpp` copied the completed backbuffer into each eye, so the interface
had no independent comfortable placement. `TexturePresenter` forced alpha to
one, and `XrRuntime` owned two eye images and submitted a single layer.
Those were concrete integration gaps, not a proven diagnosis of every bad draw.

## Reference and static evidence

Read-only reference: DiRT2VR `artifacts/prize-cars/src/trace.cpp` (`HudShader`,
`CaptureHudDraw` and overlay submission), `hud_capture.h/.cpp`, `hud_elements.h`.
It redirects matching backbuffer draws into an alpha texture on the first eye,
suppresses them on the second eye and presents a separate XR panel. It restores
render targets, blend and depth state; depth-writing geometry remains owned by
the game. Element hiding and region coordinates are DiRT2-specific and are not
appropriate defaults for DiRT3.

DiRT3 `dx11/shaderpack/ui.pssg` SHA-256:
`7728e525018485a60ce06440a10a371064985c5fb22a6a8ce5b7fb85240b92b8`.
`tools/research/inspect_ui_shaders.py` traverses its typed PSSG hierarchy and
maps SHADERPROGRAM names to DXBC code blocks. An independent raw DXBC scan
matched all 591 code offsets; 304 programs are vertex shaders. Names explicitly
containing `.ui_2d_` or `.ui_osd_2d_` map to 44 unique vertex hashes: 38 exclusive
to those names, six shared with other UI programs (41/35 before the OSD correction
below). None of DiRT2VR's six recognized HUD vertex
hashes are present. Full name/hash/offset evidence is in `ui-shaders.json`.

This is offline shipped-asset inspection, not live draw coverage. Shader names
alone do not establish screen-space behavior. Shared programs include mask
ramps, alphamasks, unlit UV and driver tags, so depth/target/transform state must
be considered before suppressing world draws. The parser is a read-only research
tool for this PSSG pack, not a general asset editor or runtime dependency.

## Next implementation boundary

Capture only verified 2D draws; keep genuine 3D frontend geometry and all cameras
stereo. Capture once per frame with complete restoration of modified graphics
state, preserving stencil masks and alpha. Present one transparent XR panel at
a comfortable distance with an appropriate UI aspect ratio, instead of leaving
the HUD baked into asymmetric eye images. Handle no-HUD frames, menus/loading,
failed acquisition and teardown without stale layers or lost game state.

## 0.1.9 implementation

The capture, shader hooks and XR overlay path are now implemented, with live
coverage and readability still pending. `hud_shaders.h` recognizes the 35
dedicated and six shared vertex hashes above. `hud_hooks.cpp` tags matching
shaders at creation using COM private data, avoiding stale pointer registries.
Seven D3D11 draw entry points route only within render-thread eye scopes.

`HudCapture` redirects first-eye draws to an RGBA8 transparent target and
suppresses their second-eye colour. It requires a single immediate-context
backbuffer target, no pixel UAVs, suitable blend state and no depth writes.
Shared shaders additionally require no effective depth test. Mask-only draws
retain their native path; colour draws which write stencil still perform those
stencil writes in each eye. Scoped target/blend restoration covers exceptions.
No backbuffer reference survives the capture frame. Empty/failed frames cannot
expose a previous HUD image. Additive/destination-dependent effects remain in
the original scene pending separate evidence.

`XrRuntime` optionally allocates a third, shared UI swapchain, bounded to 1536
pixels per dimension. Its images count toward the existing 512 MiB colour
budget. It acquires the panel only after both world eyes complete, and submits
the stereo projection followed by a transparent quad. The quad is centred
2 metres from the recentered pose, at most 1.6 metres wide and 1.8 metres tall,
with source aspect retained. F9 relocates it; other settings are deferred until
the basic interface is accepted. Allocation/acquisition failures abort the
frame rather than submitting a stale or incomplete interface.

Capture stores gamma-encoded premultiplied RGB. A separate presenter converts
it to linear straight-alpha output for XR's source-alpha/unpremultiplied flags.
The original captured image is composited into the desktop backbuffer after
both eye images have been copied, keeping the mirror interface usable.
Reference: [OpenXR quad layers](https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrCompositionLayerQuad.html).

Synthetic WARP tests cover alpha, world exclusion, first/second-eye routing,
stencil writes and exception restoration. An actual shader from the installed
UI pack also passes through the creation/draw hooks in a synthetic process;
this never launches DiRT3. XR tests cover layer ordering, physical placement,
missing/failed panels, resource cleanup and no-HUD frames. These checks do not
prove actual UI draw coverage, transformation correctness or readable menus.
The user must approve a fresh game launch for those checks.

## Attended 0.1.9 result and next diagnostic

The user confirmed readable racing HUD; frontend menus are horizontally cropped,
letterbox bars float when no menu is visible, and transitions briefly freeze with
double vision. The desktop screenshots also show the menu crop. Draw counters
and clean XR submission alone did not establish full interface correctness.

0.1.10 fixes one identified temporal-history error: only the second eye was
invalidated after discontinuities. Both slots now prime on camera cuts, actual
XR reference revisions and gaps in completed desktop frames. Loading stalls
remain a separate unknown; bounded CPU timings record XR wait and eye calls.

Offline Windows D3DDisassemble inspection of the fingerprinted UI pack shows
2D vertex programs consume CameraParamsConstantBuffer at VS slot 3, including
viewProjection at byte 64. This does not prove which transform was bound during
the reported crop. Intermediate disassemblies: `build/research/hud/*.asm`.

F8 captures one frame, only while the game is foreground, with a maximum of three
captures per process. The trace is bounded to 256 backbuffer draws, includes shader
hashes, viewport/scissor/depth/blend state, draw arguments and small prefixes of
VS/PS constants and vertex/index buffers. Input-layout metadata identifies per-vertex
and per-instance streams; direct indexed draws use their actual index/start/base
offsets. Unresolved or oversized draw windows are marked as binding prefixes,
not claimed as the draw geometry. Default blend state is reported as null.
Full staging copies larger than 4 MiB
are refused. Final captured UI is stored as lossless raw RGBA, with dimensions in
the JSONL end record. Files stay in the owned session log directory. Readback
stalls are explicit diagnostic overhead and excluded from performance acceptance.
Synthetic WARP checks verify state preservation, byte readback, image size,
foreign-target refusal, draw/capture limits and refusal to overwrite captures.

## Captured evidence and 0.1.11 correction

The user supplied complete F8 captures from PID 32868 frames 147, 518 and 1396
(19/25/17 backbuffer draws, no limit hit, each a 3072x3264 RGBA image). Evidence:
`artifacts/diagnostics/hud-0.1.10-restart-20261028`. Frame 147 is a fade; 518 shows
cropped copyright text; 1396 shows the cropped autosave warning and letterbox bars.
All 18/24/16 orthographic UI draws use three observed vertex shaders. The remaining
draw is an unclassified scene composite, outside the new correction.

The exact 320-byte camera has Px=0.2650412321, Py=0.2494505495: Py/Px=3072/3264,
so the native widescreen layout is clipped before capture. A scoped geometry shader
multiplies clip X by (Py/Px)*(9/16), about 0.5294118, before raster clipping. It
preserves all artwork varyings. Allowed VS hashes: e057d253ecd4296e,
5c571e5a5daa0ce4 and ad7b09c4361f558d. The last has TEXCOORD3 as well as
TEXCOORD0/1/2/4. A full camera fingerprint (first 308 bytes, X lens fields at
0/64/240 normalized to zero) must equal 41be88f2d87c2b12. The remaining twelve
padding bytes are uninitialized. Positive finite lens, inverse lens and viewport
aspect are also checked. Other cameras, active GS/HS/DS, stream output, effective
depth and non-fullscreen viewports are left unchanged.

Frame 1396 draws 8/9 identify the actual black bars: colour VS e057d253ecd4296e,
PS cf2fe246cd20d277, opaque black TEXCOORD2 and clip-space rectangle bounds
X=-10.6016..10.6016, top Y=0.7902..3.7723, bottom Y=-3.7836..-0.7902.
Only matching opaque-black, W=1 overscan triangles with the measured inner edge
are discarded. Fullscreen fades, bounded black menu panels, translucent/coloured
geometry and unknown pixel shaders survive. This is not a generic black filter.

UI correction uses CPU snapshots of 320-byte constant-buffer creation, immediate
UpdateSubresource and Map/Unmap writes stored as COM private data. GPU copies
invalidate snapshots. ExecuteCommandList advances a generation so unknown deferred
writes cannot leave apparently current metadata. No normal-frame GPU readback.
Each draw restores its GS and GS CB object/range, plus existing target/blend state;
qualified native stencil-only draws use the same corrected canvas in both eyes.

Corrected frames use a 16:9 quad. Its height equals the previous effective panel
height and its width expands accordingly (up to 3.2m at 2m distance), preserving
the physical size/position of existing corrected UI. Unknown-camera frames retain
the original panel dimensions. The captured menu frames do not mix corrected and
uncorrected UI; racing coverage still needs a regression check.

Synthetic WARP tests cover recovery of wholly clipped geometry, both varying
signatures, targeted bar removal and preserved black/fade artwork, exact camera
refusal, graphics-state restoration, actual CPU hooks, deferred invalidation,
HudCapture integration, physical size and mask-only stencil behavior. These do not
replace a fresh headset check. The 0.1.10 user test still showed transition doubling.

## 0.1.11 attended acceptance and next boundary

The user accepted full menu width, removed letterbox bars and readable racing HUD
in cockpit and external views. Racing logs show mixed capture (for example
76 captured draws, 15 canvas-corrected) without a reported visual HUD regression.
Intro videos show a black rectangle; transition freezes/doubling remain.
The complete session and restored-file audit are in
`artifacts/diagnostics/hud-0.1.11-20261028`.

The shipped pack has a dedicated movie VS f3c97950c737695d (with velocity/depth
variants bc345cfbdda49603 and 8a868f7f6dfba787) and HDR PS 045d037ed3de4938.
These are absent from the current HUD shader list. This is a candidate observation
point, not proof that suppressing/capturing those shaders fixes the video. Capture
actual movie targets, camera, textures and ordering relative to the black UI layer
before modifying them. Likewise identify the cached/composited source used during
a doubled transition instead of applying another speculative history reset.

## 0.1.12 automatic presentation trace

The 0.1.11 log proves two stalls outside XR waiting/eye-render calls: main 6798
has gap=1516ms, xr_wait=0ms, total=0ms; main 7114 has gap=1125ms, xr_wait=0ms,
total=16ms. No sampled stereo pair failed or lost camera restoration. This locates
those delays outside the replay block, not their root cause or the doubled image.

Existing captures' first backbuffer draw (2400 indices) is VS361f7beff48b284d /
PSd817c50518386078. The shipped `postprocess.pssg`, SHA-256
`9c927864566a69bee5329c6c37e3edefe374fe9cc760a2bb7fd5a421b785176d`, identifies
these as UniFinal vertex / final DOF pixel shaders. Inventory:
`build/research/postprocess-shaders.json`. Movie DXBC disassemblies are retained
under `build/research/movies`; the HDR movie shader samples YUV textures t0/t1/t2.

`presentation_trace` observes the known movie shaders on any target, native
backbuffer draws outside eye scopes, and small/final-composite draws within the
eyes. It records phase, Present sequence, target/texture identities/descriptors,
CPU camera shadows and depth/blend/raster/viewport state. Mod-owned presentation
passes are excluded. The first actual backbuffer binds render-thread ownership;
foreign threads/targets are refused, and no COM reference survives a call.

Diagnostic logging controls the explicit child-process flag. Output is limited
to 180 seconds, 256 unique pipelines, 12 exponentially spaced samples per pipeline,
128 changed-Present summaries and 512 total JSONL records. No GPU copy/Map/readback.
Matching texture IDs across eyes alone is not proof of stale content: native
render targets are normally reused sequentially. Use the identified pipeline to
trace writes/native cache ownership before changing it. Source also shows context
projection matrices survive ProjectionScope; downstream dependence is not yet
established, so no speculative restoration change is made in this diagnostic build.

Next approved test: allow the intro to run, then reproduce several menu transitions
within the first two minutes. No F8 is needed. No launch is authorized until the
user approves the verified 0.1.12 package explicitly.
# 0.1.12 attended trace and 0.1.13 candidate

The user approved one 100% VDXR launch and reproduced both defects. Session
`a3e0ada6a33e4761b3a4d291ae739539`, PID 32504, ran from 19:13:22Z to clean XR
shutdown at 19:16:15Z on 2026-10-28. Evidence is preserved under
`artifacts/diagnostics/presentation-0.1.12-20261028`, including the automatic
JSONL, native log, structured findings and exact file-restoration receipt.

The intro draw is not one of the `ui.pssg` movie shaders. Captured VS
`65de59dd5b27d083` / PS `443b53fbfc6b08fb` draws four vertices to the backbuffer
outside the stereo-eye scopes, using distinct R8 Y/U/V textures with half-size
chroma. It happens after the eye copies and old XR submission boundary. The
first sample uses opaque GE depth; later samples disable depth and use alpha.

The in-eye transition is VS `ac773a7adea2a83b` / PS `d0a420252e5afa9f`, four
vertices targeting the backbuffer. Shipped `shaderpack.pssg` SHA-256
`644c069f0bc38faa8e64f53baaf7bd7af40638fe366d63edb133f043c55d9d46`
identifies its pixel program as `screen_transition.cg.fp`. Offline DXBC
disassembly shows TScreenGrab/TScreenGrab2 selected by CB3.leftEye, with alpha
from ScreenGrabInverseDimAndLerp. Both captured eyes set leftEye to 1 and select
the same full-size screen grab. Two measured variants are source-alpha without
stencil, and opaque with read-only EQUAL/KEEP stencil at reference 1. Resource
identity alone cannot prove the last writer; the shader establishes the cached
screen blend, separately from game-loading pauses.

0.1.13 defers interactive XR submission to Present, preserving one frame and
its predicted poses. A recognized late movie uses the current overlay image as
an opaque shared 16:9 screen; a complete stereo pair is still required, so this
cannot hide failed eye rendering. Ordinary frames retain the existing stereo
world and HUD panel. Shutdown/failed acquisition discards the pending frame.

Within stereo eye scopes only, the exact transition shader and measured target,
source and pipeline states are skipped, producing a direct cut. Unknown states,
foreign targets, stencil-writing draws and desktop-only rendering stay native.
No new texture history or game patch is introduced. Automated checks and a fresh
approved headset run are required before acceptance. Native loading stalls may
remain; this change specifically removes the incompatible captured-screen blend.

## 0.1.13 feedback and 0.1.14 correction

The attended 0.1.13 run displayed the intro videos and the user reports that the
doubled/frozen transitions seem gone. Two remaining defects are confirmed by
screenshots: the final copyright plate leaves screen mode early, and racing HUD
instruments stretch horizontally in both the headset and desktop mirror. Evidence
and restoration receipt are in `artifacts/diagnostics/presentation-0.1.13-20261028`.

The Windows `states.bin` flow plays AMD_sting.bik (182 frames), then sting.bik
(148 frames), before `screen_initial_legal` and `press_start`. Do not substitute
the differently structured `states_steam.bin` flow. Raw movie detection drops for
one frame between the clips, and ends before the native legal screen completes.
The screenshot identifies that final screen as `ScreenInitialLegal`.

Read-only disassembly identifies its enter/leave functions at preferred addresses
0x4e0820/0x4e09d0, vtable 0xe95370 slots 0x38/0x44, and type string 0xe9a9f4.
Both callbacks use void __thiscall(self). The enter installs the initial-legal
overlay; leave removes it after the game's own timing/fades. Research and bounded
disassemblies are retained in `build/research/intro-handoff`.

0.1.14 observes those two callbacks under the existing executable SHA-256 guard,
plus loaded entry-byte, rebased vtable, constructor-pointer and type checks. An
atomic startup latch holds the shared screen from the first observed movie (or
legal enter when movies are skipped) through legal leave. Completion cannot be
undone by a later movie; later recognized video draws select a screen only for
their current frame. No timer or new texture allocation is introduced. Tests
cover inter-clip gaps, skipped movies, repeated/concurrent callbacks, unsupported
hosts, mutated signatures and a rebased executable fixture.

The HUD capture previously selected a wide panel whenever any draw used the menu
camera correction. Racing frames also contain those draws, so native-aspect
instruments were stretched with the entire texture. The shared canvas now has a
stable aspect of max(16:9, native aspect). Already-admitted native HUD draws fit
horizontally inside it through scoped viewport/scissor changes, preserving their
original clip bounds and displayed proportions. The fingerprinted menu geometry
correction and letterbox removal remain in place. Synthetic mixed-frame tests in
both draw orders verify a square instrument remains square after presentation,
while menu coverage and state restoration survive ordinary and throwing draws.

All 23 native, 545 launcher and 53 diagnostic/recovery checks pass. These changes
were subsequently accepted by the user in the approved 0.1.14 retry: the complete
intro/splash sequence finishes, HUD proportions are normal, and transitions stay
clear. The remaining frontend triangle-shadow defect is separate; see
[frontend shadows](frontend-shadows.md).


## Race display (OSD) shader coverage

During VR racing a cyan and black rectangle stayed at the top of the headset
view and followed head movement. The 0.1.14 racing trace
(`artifacts/diagnostics/presentation-0.1.14-20261028`) records only uncaptured
draws inside eye scopes (`hud_hooks.cpp` traces `HudShaderKind::None` while a
capture is current). Apart from the final DOF composite, the only screen-space
draws left in both eyes are three race display programs:

| Vertex shader | Program | Pixel shader |
| --- | --- | --- |
| `15472d78cb54f392` | `uiio_osd_2d_image` | `ba80fd4101a15296` |
| `f89762721daadb0b` | `uiio_osd_2d_colour` | `ecf67d6e4a1d06d2` |
| `1d3d792d462f6733` | `uiio_osd_2d_image_scroll` | `523ffb99f2bb4a53` |

The inventory matched only `.ui_2d_`, so the `ui_2d_osd_*` family was captured
but the word-swapped `ui_osd_2d_*` family was not. None of these vertex hashes is
used by a non-OSD program. The traced draws use the UI camera, a full viewport,
no depth test and ordinary source-over blending, so the existing capture admits
them unchanged. The inventory now also matches `.ui_osd_2d_`; `hud_shaders.h`
classifies the three as dedicated two-dimensional HUD shaders. The shipped-pack
contract test and shader-hook test verify the regenerated list.

Confirmed: in the attended 0.1.20 VDXR session on 2026-10-31 (PID 3872, LA
Coliseum rallycross) the log tagged all three vertex shaders as HUD (`kind=1`) and
the user reported that the rectangle is gone. That session's racing HUD uses
cyan-and-black OSD elements (position list highlight, boost meter), consistent
with this cause.
