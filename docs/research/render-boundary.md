# DiRT 3 render boundary — static findings, 2026-10-28

The DiRT 2 outer/inner rendering split has a close structural equivalent in the
installed DiRT 3 executable. DiRT 3 also contains its own conditional second call
to the inner renderer using the same prepared list header. This is strong
evidence that the proposed architecture is feasible. It does **not** establish
that arbitrary eye replay, changed camera poses, or OpenXR presentation is safe.
The milestone remains gated on observed execution and restoration tests.
The later [passive-observer follow-up](observer-followup.md) records the first
runtime samples, concrete header writers, and corrected benchmark file lookup.

## Identity and reproduction

- Input: `C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition\dirt3_game.exe`
- SHA-256: `c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`
- x86 PE32, image base `0x400000`. Addresses in this report are preferred virtual
  addresses; runtime addresses are module base plus the listed RVA.
- Method: PE file-byte validation, GNU objdump static disassembly, and an
  independent Ghidra 12.1.4 function dossier via REA 6.4.0. The game was not
  executed or attached to during this investigation.
- Reproduce: `python tools/research/inspect_render_boundary.py <dirt3_game.exe>
  --output build/research/static-evidence --objdump <objdump.exe>`.
- The script refuses other fingerprints, verifies direct call targets, and
  emits file offsets and per-range hashes. The checked-in
  [evidence manifest](static-evidence/static-render-evidence.json) and adjacent
  focused disassemblies are the source evidence for the observations below.
- Ghidra independently confirms the inner body `0x984a50..0x984b3d`, six stack
  arguments with a final character flag, a void return, and outer `0x98a270`
  as its sole identified caller. [Selected Ghidra view](static-evidence/inner-ghidra-view.json),
  evidence `ev_60bde0ba3bf2b2e5103ee4d866b30e36d16803e5794184c74b9c311f2d85ab52`.
  Pseudocode is analysis output, not recovered source. The original full dossier
  and reusable snapshot remain under `build/research/`. The initial sandboxed
  CLI attempt failed private temporary-file admission; the approved read-only
  query completed outside that sandbox with a 1,200-second startup budget.
- A second independent dossier confirms the complete outer body
  `0x98a270..0x98a446`, five stack arguments, and its conditional second inner
  call with the same 264-byte header. [Selected outer Ghidra view](static-evidence/outer-ghidra-view.json),
  evidence `ev_79e25ca49491fec1dc159c019720dcdf58a6d0f5f1e371917f0735e7ffb167df`.
  The static manifest verifies six direct outer call sites; Ghidra associates
  four of them with identified caller functions. This is a function-discovery
  limitation, not evidence that the other two calls are absent.

## Matched boundaries

| Role | DiRT 3 VA | RVA | Evidence |
|---|---:|---:|---|
| Outer scene | `0x98a270` | `0x58a270` | [outer_scene.asm.txt](static-evidence/outer_scene.asm.txt) |
| Inner prepared scene | `0x984a50` | `0x584a50` | [inner_scene.asm.txt](static-evidence/inner_scene.asm.txt) |
| Main renderer method | `0x580540` | `0x180540` | [main_scene_caller.asm.txt](static-evidence/main_scene_caller.asm.txt) |
| Main-to-outer return | `0x580650` | `0x180650` | Call at `0x58064b` |
| First inner return | `0x98a388` | `0x58a388` | Call at `0x98a383` |
| Conditional second inner return | `0x98a41d` | `0x58a41d` | Call at `0x98a418` |
| Camera/context setup | `0x97a9b0` | `0x57a9b0` | [camera_setup.asm.txt](static-evidence/camera_setup.asm.txt) |
| Prepared list header construction | `0x96e9a0` | `0x56e9a0` | [list_header_constructor.asm.txt](static-evidence/list_header_constructor.asm.txt) |

The outer method uses `__thiscall` with five stack arguments and returns with
`ret 0x14`. The inner method takes six stack arguments and returns with
`ret 0x18`. Names such as `context`, `scene`, and `flags` remain analyst labels;
the argument types are not recovered C++ types. The inner final argument is a
local flag value produced by preparation, not the outer fifth pointer.
All six identified direct outer call sites replace ECX and call the prepared
object release helper without consuming EAX. Both direct inner call sites
also ignore EAX. Together with the decompiler output, this supports void
pass-through wrappers for these observed callers; it is not an exhaustive
claim about unknown indirect callers.

For comparison, the DiRT2VR source uses inner RVA `0x336be0`, outer RVA
`0x33ad10`, and main return RVA `0x2889c0`. Those addresses, signatures and
structure sizes must not be carried into this project.

## Layout established from instructions

The main renderer constructor at `0x58ac30` installs vtable `0xeac84c`
(RVA `0xaac84c`) and constructs two camera objects at `self+0x600` and
`self+0x690`. Its allocation at `0x5cb6eb` is `0x9d0` bytes. This type/size
does not describe every renderer that calls the shared outer method.

Each camera is **0x90 bytes**, including a vtable/padding region before the
four pose vectors. Its constructor at `0x47bfe0` writes vtable `0xe85b50`
(RVA `0xa85b50`), vector slots `+0x10`, `+0x20`, `+0x30`, `+0x40`, and scalar
parameters from `+0x50` through `+0x88`; a flag is at `+0x8c`. The outer method
explicitly copies `0x90` bytes for its second pass at `0x98a3c3`. Camera pose
interpretation must still be checked in a live scene; copying DiRT 2's first
112 bytes would truncate this object.

Constructor constants further distinguish the vector order: `+0x10` starts
at `(0,1,0,0)`, `+0x20` at `(1,0,0,0)`, `+0x30` at `(0,0,1,0)`, and `+0x40`
at zero. Do not label the first vector as right without verifying it. Scalars
default to `pi/2` at `+0x50`, `0.2` at `+0x54`, `1000` at `+0x58`, `0.05` at
`+0x84`, and `4` at `+0x88`. These are observed values; their full semantics
remain unproven by this static pass.

The prepared list header occupies **0x108 bytes**. Its constructor initializes
fields through `+0x104`; the outer stack camera copy starts exactly `0x108`
bytes after the header. This is a header containing pointers, not a deep copy
of render data. Preparation helpers sort/relink underlying collections before
the inner call. The header must not be reused after the outer call returns.

The setup function writes the supplied context through `+0x1cf`, so its
observed minimum accessible extent is **0x1d0 bytes**. It writes matrices at
`+0x70`, `+0x110`, `+0x150`, `+0x190` and other fields, and invokes context
virtual methods. Neither a complete context size nor all indirect mutations
has been established.

## What the native second pass establishes

The outer function performs view setup, virtual preparation calls, list
construction, and list population before calling the inner function. It then
conditionally performs a second inner call when renderer type `+0x14` is
`0x400` or `0x404` and the device's virtual method at `+0xd0` returns true.
The branch:

1. Copies the second input camera to a 0x90-byte stack object and clears its
   `+0x8c` flag.
2. Calls `0x8a8c60` with zero and a second interface method at vtable `+0x68`
   with one.
3. Calls the same inner function with the same prepared list header and first
   camera, substituting the copied second camera.
4. Calls those mode switches again with one and zero respectively.

`0x8a8c60` stores a flag at device `+0x4d74`; when device `+0x4d78` is non-null,
it also dispatches a driver query wrapper using ID `0x96eea9f8`. This is
consistent with a native stereo eye switch, but this investigation does not
claim that the XML stereo flag alone activates the branch on this machine.
The branch does not show a second simulation update, but indirect callees were
not exhaustively audited. It is not proof that replay is free of side effects.

## Concrete replay hazards

The later [eye replay transaction](eye-replay-transaction.md) resolves the
list rollback question and identifies the native velocity-history protocol.
Use that protocol for the bounded experiment; this section records the original
hazards that motivated it.

- The main colour dispatch at `0x5804c0` normally calls `0x96e630`, which
  **increments renderer `+0xc4`** at instruction `0x96e654`. A second inner
  invocation advances that serial, just as the native two-eye branch does.
  Preserve this per-render progression; do not confuse it with the separate
  velocity plugin frame serial, which has a once-per-pair camera flag.
- Camera setup changes the context and GPU state. Its virtual calls can change
  state outside the visible object. Restoring just camera bytes is insufficient.
- The main caller releases its prepared object with `0x96e970` and clears
  `self+0x720` immediately after the outer returns. Delayed frame replay cannot
  retain that pointer.
- The list header references shared intrusive collections and render resources;
  unchanged header bytes do not prove unchanged pointees or reusable GPU data.
- Native stereo modifies only its copied second camera and relies on device
  stereo state. Moving both cameras for arbitrary head poses adds culling,
  lighting and reflection requirements absent from that static observation.
- Shared outer callers include reflections and other renderers. Guarding only
  the inner address is insufficient to identify the displayed main view.

## First runtime diagnostic contract

An explicitly enabled observer may install **pass-through hooks only** after
the executable hash and both full entry signatures match:

```text
outer: 53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04
inner: 55 8b ec 56 8b f1 80 7e 55 00 74 16 8b 4e 38 85
```

Classify a main outer invocation by all of: return RVA `0x180650`, renderer
vtable RVA `0xaac84c`, and exact inline camera relationships `+0x600/+0x690`.
Use thread-local nesting state so an inner invocation is associated with its
owning outer. The second native inner call can have a stack camera, so its
camera-pointer equality must not be required for ownership classification.

Record bounded first/every-120 samples: frame/present serial, thread ID,
outer/inner counts, inner return RVA, pointer relationships, render serial
`self+0xc4`, and before/after hashes of the readable camera/header ranges.
Use checked readable regions plus fault-contained reads; skip unreadable data.
Do not traverse list pointees, invoke the engine a second time, write any game
state, or infer simulation safety from call counts. Leave native stereo off for
the first diagnostic run.

Acceptance of this observer is limited to proving where the real main render
calls occur and which inline bytes change. The replay gate additionally needs
same-simulation-frame eye evidence, scoped CPU/GPU restoration, visibility and
effect checks, repeated stage transitions, and attended VDXR/SteamVR acceptance.
