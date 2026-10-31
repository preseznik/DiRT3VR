# DiRT 3 eye replay transaction

Static follow-up, 2026-10-28. Applies only to executable SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.
Addresses in prose are preferred VAs, image base `0x400000`; the guard table
uses RVAs. No game was executed or attached by this investigation.

**Implement the pair inside the first main-view inner call, retaining the live
prepared lists. Do not rewind or rebuild the lists, owner flags, or renderer
counter between eyes. Preserve the native second-eye velocity history protocol.**
This is a concrete basis for a bounded rendering experiment. It is not visual
acceptance of arbitrary tracked poses, effects, or a headset runtime.

## What differs from the DiRT2VR port

DiRT2VR `src/trace.cpp`, `HeadsetScene` and `ContinuousScene`, passes the same
`lists`, `context`, `scene`, and `flags` to both `realInner` calls. It restores
the modified camera vectors and explicitly refreshes captured light materials;
it does not restore a snapshot of the list header, renderer, or full context.
Its 112-byte cameras and contexts are not DiRT 3 layouts.

DiRT 3 supplies stronger native evidence for list reuse: outer `0x98a270`
calls inner `0x984a50` at `0x98a383`, then optionally calls it again at
`0x98a418`, using the same stack list header and same context, shadow context,
camera A and preparation flag. It does not call the preparation methods again,
construct another header, reset a counter, or undo any first-pass mutations.
The normal renderer type is `0x400`; driver stereo availability additionally
controls this native branch.

DiRT 3 also has a velocity/depth plugin with two camera-history slots. Merely
calling inner twice with camera B's flag unchanged advances its frame serial
twice and uses the wrong history slot. These details cannot be copied from
DiRT2VR's shorter camera structure.

## Exact argument roles and lifetimes

Names here describe observed usage, not recovered C++ class names.

| Argument | Main invocation provenance | Observed role |
|---|---|---|
| `self` | Main renderer vtable `0xeac84c` | Owns inline cameras and render resources |
| Inner `lists` | Outer stack, `0x108` bytes | Published pointers into the prepared object; keep live and mutable |
| `cameraA` | `self+0x600` | Preparation/shadow camera; the inner shadow stage receives this camera |
| `cameraB` | `self+0x690` | Depth/velocity and colour view; setup runs twice with it inside inner |
| `contextC` | `*(self+0x5f4)` | View/graphics parameter object updated by `0x97a9b0`; depth, AO and colour stages receive it |
| `contextD` | Global pointer at `0xff7e48` | Auxiliary context passed to shadow preparation/render; the alternate preparation branch also calls view setup on it with camera A |
| Outer `preparedE` | `*(self+0x720)` | Owning prepared collection used by the header constructor; released immediately after outer returns |
| Inner `flagE` | Outer local byte, passed in a stack DWORD | Result of shadow preparation; only low byte is tested by inner. It is not `preparedE` and must be forwarded unchanged |

`0x96e490` initializes the flag true, then may replace it with the return of
the shadow interface's vtable `+0x28`. `0x96e510` receives `contextC`,
`contextD`, `cameraA`, and `lists+0xf4`; it calls that interface's render
method at `+0x2c`. The complete interface and context object sizes remain
unrecovered. None of these pointers is an independent simulation-frame copy.

The view setup writes parameter matrices through `contextC+0x1cf`, including
`+0x70`, `+0x110`, `+0x150`, `+0x190`. The velocity plugin consumes the matrix
at `+0x190` for its history. Treat setup as the producer: let each inner call
rebuild the context for its eye. Do not memcpy a guessed `0x1d0` object back,
because setup has virtual/GPU effects. The later projection investigation
identified this exact object as a 0x1d0-byte CAMERANODE; knowing its allocation
size does not make a full-object rollback valid. See
[asymmetric projection](asymmetric-projection.md).
The native pair itself leaves the context in the last rendered view.

Evidence: [main caller](static-evidence/main_scene_caller.asm.txt),
[outer](static-evidence/outer_scene.asm.txt),
[inner](static-evidence/inner_scene.asm.txt),
[depth stage](static-evidence/inner_depth_stage.asm.txt),
[shadow preparation](static-evidence/outer_shadow_prepare.asm.txt),
[shadow render](static-evidence/inner_shadow_stage.asm.txt),
[view setup](static-evidence/camera_setup.asm.txt).

## Why the changed list header should survive eye one

The late writer `0xc16c20` is a lazy preparation operation, gated by the
owner's byte `+0x40`. It first checks a phase flag at owner `+0x29be` against
its second argument, then **clears owner `+0x40` before generating anything**
(`0xc16c52`). The normal path calls `0xc16ba0`, sorts the owner `+0x34`
container, and publishes the resulting roots to `lists+0x84` and `+0xd8`.

The main outer preparation method `0x580a80` stores the prepared object's
containers at owner `+0x34/+0x38`, other preparation parameters at `+0x10..+0x3c`,
and arms owner `+0x40=1` at `0x580c50`. The main depth and colour stages call
the writer with phase 1 and 0 respectively. They do not rearm it. Therefore a
second inner invocation observes the already published lists and skips this
one-time generation. This explains the observed changing `+0x84..+0x87`
header bytes without implying that eye two has lost ownership.

There are real pointee mutations, not merely pointer substitutions:

- `0x8c6510` sorts/relinks list nodes and publishes the root at container
  `+0x8`, along with maximum/aggregate values at `+0x0c/+0x10`.
- `0x8c67d0` adds aggregate values to `lists+0x4`, updates the maximum at
  `lists+0x0`, and returns the published root. Repeating preparation can
  double-count these aggregates even if the root is unchanged.
- `0x8a33d0` flattens tree nodes by changing next/child links, and
  `0x8a3440` stores the resulting root in its container `+0x4`.
- The owner `+0x41` alternate path can clear `+0x84/+0xd8` after cleanup;
  rewinding the header could resurrect references the native path cleared.

Thus a header-only rollback is wrong. Restoring owner `+0x40` is also wrong:
it replays generation while the pointees, aggregates and potentially GPU work
have already changed. Preserve all those post-first-pass effects for eye two,
just as the stock second-eye branch does. Do not retain them after outer exits.

Evidence: [outer preparation](static-evidence/main_prepared_finalize.asm.txt),
[late writer](static-evidence/late_list_header_writer.asm.txt),
[late generator](static-evidence/late_list_generate.asm.txt),
[sort/publish](static-evidence/list_sort_and_publish.asm.txt),
[aggregate update](static-evidence/list_aggregate_update.asm.txt),
[tree flatten](static-evidence/tree_list_flatten.asm.txt),
[tree publish](static-evidence/tree_list_publish.asm.txt).

## Native velocity/depth second-eye protocol

Main renderer `self+0x2c` points to `VelocityDepthPlugin`, whose verified
vtable is `0xeca3a8`. The provenance is factory `0x733030`, which constructs
`0x1f0` bytes via `0x732a70` and stores its pointer in factory `+0x9c`
(global `0xfa1df8`). The renderer factory passes this as its plugin, and the
base renderer constructor stores it at `+0x2c`.

| Plugin field/method | Meaning established from code |
|---|---|
| `+0x10..+0x4f` | History matrix for eye selector 0 |
| `+0x50..+0x8f` | History matrix for eye selector 1 |
| Byte `+0x90` | History valid flag; constructor clears it |
| DWORD `+0x94` | Velocity frame serial; incremented only if input camera B byte `+0x8c` is nonzero |
| DWORD `+0xa0` | Eye selector, initialized 0 |
| vtable `+0x44`, `0x72c9c0` | Depth/velocity setup: consumes previous selected matrix, produces per-view parameters, then stores current selected matrix |
| vtable `+0x60`, `0x732c50` | Clears only history-valid byte `+0x90` |
| vtable `+0x68`, `0x732d90` | Stores its DWORD argument at selector `+0xa0` |

At `0x72c9e2..0x72c9f1`, camera B `+0x8c` controls the serial increment.
At `0x72ca79..0x72cadc`, the plugin selects matrix `plugin+0x10+index*0x40`.
At `0x72d1dd..0x72d212`, it stores the current context matrix
`contextC+0x190..+0x1cf` back into that slot. The stock outer clears the copied
second camera's `+0x8c` and selects index 1 for its second call, then returns
the selector to 0.

Camera B `+0x8c` also selects the sign of the native stereo projection at
`0x97af1e`. That path is gated on device stereo availability and mode flag
mask 2 (bit index 1). With driver stereo disabled, clearing this byte for the second eye
still matters for velocity frame ownership even though the projection-sign
branch is inactive. Do not call the NVAPI-facing device switch `0x8a8c60` for
the OpenXR/desktop replay experiment.

**Initialize the newly used history slot.** The constructor does not initialize
both history matrices. An existing mono session has only used slot 0. Before
the first second-eye render of a contiguous pair sequence, select slot 1 and
call the verified invalidation method. With `+0x90=0`, setup takes its native
branch at `0x72cc35`: it primes the selected slot from the current context and
sets the valid flag. No uninitialized slot is read. Repeat this priming when
the plugin instance changes or stereo resumes after an unpaired interval.
Keep the resulting history matrices and validity; they belong to the engine.

Evidence: [factory](static-evidence/velocity_depth_factory.asm.txt),
[constructor](static-evidence/velocity_depth_constructor.asm.txt),
[setup including prime/store](static-evidence/velocity_depth_setup.asm.txt),
[invalidate](static-evidence/velocity_history_invalidate.asm.txt),
[selector](static-evidence/velocity_eye_select.asm.txt),
[history accessor](static-evidence/velocity_history_get.asm.txt).

## Bounded experiment and restoration protocol

1. Require the exact executable/entry guards, main outer identity and main
   first-inner return (`RVA 0x58a388`), non-nested ownership, both original
   inline cameras, the verified plugin/vtable/methods, and selector 0. Require
   driver stereo off and an independently verified serial update/render mode.
2. Save only the camera pose fields being modified, camera B byte `+0x8c`,
   and the plugin selector. Preserve both camera vtables and opaque fields.
   Initially use a bounded fixed eye separation, followed by captures from a
   verified stage. A frontend-only sample does not establish race rendering.
3. For eye zero, select history slot 0, retain camera B's original flag, apply
   the scoped eye camera and call the original inner once with all live
   arguments unchanged. Restore the changed camera fields immediately after.
4. For eye one, apply its scoped camera, clear B `+0x8c`, select slot 1, prime
   the slot if necessary, and call the original inner once. Use the mutated
   live header from eye zero. Restore the camera fields and original selector
   even if a capture or submission fails.
5. Retain the engine's list mutations, histories, context and render serial.
   Restore any separately modified projection bindings or mod GPU state. If
   image capture is only `CopyResource`, it does not require binding different
   game shaders or render targets. Do not perform a third render merely because
   an eye copy failed; a partially rendered pair is already consumed.
6. Check both camera records exactly restored, selector restored, two inner
   calls, no Present inside the pair, nonzero draw statistics and separate eye
   images. Check velocity serial after eye one equals that after eye zero.
   These validate the transaction's frame ownership, not physics speed or all
   visual effects. Track simulation/gameplay acceptance separately.

Renderer `self+0xc4` is incremented by the colour stage `0x96e630`. The native
two-eye branch also increments it twice and never rewinds it. Treat it as a
per-render diagnostic serial, not a simulation frame number. Its complete
downstream meaning has not been recovered; **do not write it back to make a
pair appear to have one render**. Velocity plugin `+0x94` is a separate field
with an explicit native once-per-pair rule.

## Guards added to the static evidence manifest

| Method | RVA | Complete/specified entry bytes |
|---|---:|---|
| Velocity eye selector | `0x332d90` | `55 8b ec 8b 45 08 89 81 a0 00 00 00 5d c2 04 00` (entire function) |
| History invalidate | `0x332c50` | `c6 81 90 00 00 00 00 c3` (entire function) |
| Velocity setup | `0x32c9c0` | `53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04` (first 16; common prologue) |
| Velocity plugin vtable | `0xaca3a8` | `+0x44=base+0x32c9c0`, `+0x60=base+0x332c50`, `+0x68=base+0x332d90` |

The whole velocity setup range `[0x72c9c0,0x72d220)` is `0x860` bytes with
SHA-256 `ada58ea4f7a830665341ec634a4070597114beef1092d931d7d01b3b57e6672c`.
The [manifest](static-evidence/static-render-evidence.json) records all function
ranges and vtables. Regenerate with `tools/research/inspect_render_boundary.py`.

High confidence: native list reuse, lazy list-owner protocol, camera flag and
velocity slot selection, history invalidation/priming, restoration boundary.
Still needs runtime evidence beyond the bounded desktop result below: image/depth quality,
visibility at tracked poses, lighting/material refresh, reflections, resets,
stage transitions, simulation timing and both OpenXR runtimes. No new REA
session was opened for this follow-up; the existing disassembly was sufficient.

## Bounded desktop pair result, 2026-10-28

The implementation run
`artifacts/diagnostics/20261028-104835-08735836/run.json` records a completed
Aspen benchmark, clean exit 0 after 130.12 seconds, 120 valid replay pairs,
restored cameras, stable Present sequence within each pair, and velocity history
advancing once on the first eye and remaining unchanged on the second.
Three pairs have pipeline statistics and six BMP captures. The parent task's
image review found the expected eye separation/parallax. This is evidence for
bounded two-view rendering from the retained prepared scene, not headset or
general gameplay acceptance. `stereoVerified` remains false in the run record.

| Scene | IA primitives, first/second eye | Pixel shader invocations, first/second eye |
| --- | --- | --- |
| 792 | 527632 / 604211 | 7902176 / 7916493 |
| 852 | 380479 / 390742 | 7520041 / 7513604 |
| 911 | 467865 / 603563 | 7416482 / 7437163 |

Unequal IA counts are consistent with a concrete native pass difference:

1. The first prepass calls the late generator with phase 1 at `0x580fde`.
   When owner byte `+0x29be` is zero, the generator returns before publication.
2. That prepass nevertheless renders the current `lists+0x84` at `0x580ffb`,
   through `VelocityDepthPlugin.vtable+0x4c`. The method at `0x71ba00` forwards
   the list to `0xa1b9b0` with the plugin's depth/velocity material parameters.
3. The first colour stage calls the generator with phase 0 at `0x581498`,
   publishes the updated root, and draws `lists+0x84` at `0x5814c3`.
4. The second prepass receives the already published root and can therefore
   submit additional primitives. Both colour stages can draw the published list.

The initial root is read from prepared container `+0xa8` by the list-header
constructor; it is **not necessarily empty**. The late generator can add or
change its nodes. This ordering is also used by the stock second-eye branch.
It supports retaining the first eye's list mutations and does not justify
rewinding the header, forcing early generation, or modifying flags merely to
make the counters equal.

The run's statistics cover the complete inner invocation; they do not attribute
the extra primitives to a particular pass or prove that every visible surface
matches. The late-list explanation is supported by static control flow, not a
measured per-pass attribution. Remaining visual risks include unequal depth,
velocity or AO contributions and effect-specific geometry. Nearly equal pixel
shader counts and visibly distinct images do not rule those out. If visual
testing finds a defect, the discriminating probe is separate depth/colour
pipeline statistics plus `+0x84` root observations before/after those stages.
No runtime change was made in response to the aggregate count difference.

The history counter's exact arithmetic is also established: `0x72c9f1` adds
one to a DWORD, naturally wrapping modulo 2^32, only when camera B byte `+0x8c`
is nonzero. The depth-stage call is skipped when renderer **byte** `+0x50` is
zero or the plugin is null. With a verified non-null plugin, the expected first
inner delta is `(+0x50 != 0 && originalB[0x8c] != 0) ? 1 : 0`; the second delta
is zero because the scoped second camera disables the flag. This establishes
velocity-history ownership, not the simulation tick count.

Additional evidence: [prepass consumer](static-evidence/main_prepass_late_list_consumer.asm.txt),
[colour consumer](static-evidence/main_colour_late_list_consumer.asm.txt),
[depth list draw](static-evidence/velocity_depth_list_draw.asm.txt).
