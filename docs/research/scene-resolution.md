# Native scene-size enforcement

Target: Steam Complete Edition `dirt3_game.exe`, SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.
Image base is `0x400000`; production guards use RVAs.

## Why the hook is needed

The approved 0.1.7 retry, PID 23596 on 2026-10-28, requested 3072x3264
at 100% for Quest 3 through VDXR. DiRT 3 created a 1280x720 backbuffer and
rewrote its hardware XML. The runtime correctly refused to start VR with that
mismatch. Evidence: `artifacts/diagnostics/resolution-0.1.7-20261028/retry`.

GRID2VR's `scene_resolution.cpp/.h` at commit
`cc481c679ffa8ee0391291d001ad83349388b4ca` supplied the transactional approach.
DiRT 3 offsets below were derived independently with offline PE disassembly.
REA opened the fingerprinted executable; the instruction evidence here comes
from GNU objdump and the source-bound Python binary-contract test, not a REA
decompiler result. Neither inspection executes the game.

## DiRT 3 renderer layout

| Offset | Field | Static evidence (VA) |
|---|---|---|
| `0x0c` | Fullscreen byte | `0x7c8f2e` uses it to set descriptor Windowed |
| `0x30/0x34` | Engine width/height | `0x7c8eca` copies them to descriptor |
| `0x78` | HWND | `0x7c8efe` reads it for OutputWindow |
| `0xf0/0xf4/0xf8` | Device/chain/context pointers | Device call output and factory call arguments |
| `0xfc` | DXGI_SWAP_CHAIN_DESC | `0x7c8eb5` constructs descriptor pointer |
| `0x164/0x168` | Windowed width/height | `0x7c8da2` copies engine dimensions |

Creation calls factory vtable slot 10 at `0x7c903d`, returning to `0x7c903f`.
The chain output is renderer+0xf4. Recreation calls the same API at `0x7c8501`,
returning to `0x7c8503`, but its output is a stack local: the game copies it to
renderer+0xf4 **after** returning. This differs from GRID2VR's output guard.
ResizeBuffers is vtable slot 13, called at `0x7c7e14`, returning to `0x7c7e16`.
Its width/height arguments come from the renderer's descriptor.

`src/scene_resolution.cpp` checks these instructions in loaded memory, as well
as the exact hash already checked by diagnostics, before accessing game fields.
Initial creation requires the known caller, matching writable fields, the
process-owned NeonClass_41 window, windowed single-sample 10-bit buffers, matching
device and output identity. Resizes and recreation additionally require the
same renderer, device, window and thread established by initial creation.
Only six size words change: engine, windowed and descriptor width/height.
Failed DXGI operations restore those words only if their applied values remain.
Desktop launches, unrelated calls, unsupported builds and benchmark runs retain
their existing behavior. No COM references survive to obstruct recreation.

## Validation and remaining gate

`scene_binary_contract` verifies every instruction signature against the installed
file without executing it. `scene_resolution_transactions` tests synthetic
creation, failed allocation, rollback, subsequent edits, resize, recreation,
released-chain refusal, invalid dimensions, wrong caller, wrong thread, changed
instructions, foreign HWND and mismatched renderer fields.

All 16 native tests pass. These do not establish real internal render-target
sizes, frontend aspect ratio, memory use, reset behavior or headset appearance.
The existing actual-backbuffer and headset-recommendation checks remain active.
A fresh approved launch must show guarded creation, the requested backbuffer,
XR submission and correct menus/driving before step 3 is accepted.

Recovery deliberately still rejects semantic XML changes outside its exact
applied file. The 0.1.7 retry changed width, height and refresh rate; it required
review before exact restoration. No heuristic blanket permission for future
game-written or user-edited XML was added.
