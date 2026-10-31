# Frontend triangle shadows — 0.1.15

The user accepted 0.1.14 intro completion, HUD proportions and transitions, then
reported that the yellow frontend pyramids' ground shadows move with the headset.
The screenshot is preserved in
`artifacts/diagnostics/presentation-0.1.14-20261028/retry-1/frontend-shadow-user.png`.

## Native evidence

Installed Steam executable SHA-256:
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.
Installed `dx11/shaderpack/shaderpack.pssg` SHA-256:
`644c069f0bc38faa8e64f53baaf7bd7af40638fe366d63edb133f043c55d9d46`.

The typed PSSG inventory, original DXBC, shader disassembly and full native
producer disassembly are in `build/research/frontend-shadows`. REA's Ghidra
decompilation corroborates the native disassembly; no executable was modified
or launched for this investigation.

- Native producer VA `0x57cc20` is a `thiscall` with one stack argument. Main
  renderer call VA `0x5805eb` passes `renderer+0x5f4` as the camera context and
  `renderer+0x9c0` as the producer owner, before the stereo scene boundary.
- It updates the main camera's cached inverse view, reads its four rows at
  context offsets `0x70..0xa0`, multiplies by the frontend shadow camera's VP,
  then applies texture scale/bias. Stores at VA `0x57d0e2..0x57d113` write the
  resulting 64 bytes to owner offset `0x760`. Texture/camera absence returns
  without writing, so the observer refuses those cases.
- After those stores, the function renders and blurs using the separate shadow
  camera. It does not write or pass the original main context again. Capturing
  that context's inverse view after the producer therefore records the view
  used to build the matrix.
- Material setup around VA `0x587ed7` binds `feParticleCameraInverse` through
  owner parameter object `+0x740`, whose data starts at `+0x760`.
- Shared shadow VS hash: `4b2b0f44adfbf5bb`. Frontend-only PS variants:
  `a40172fc4329f5f6`, `6299a6c46af41b38`, `28625f0662b53c50`.
  Their 240-byte PS constant buffer 0 stores this matrix at bytes 48–111.
- These pixel shaders reconstruct view position with the current eye's inverse
  projection, then use the separate cached matrix to sample `feParticleTexture`.
  Thus the original camera-relative mapping becomes stale under eye movement.

DiRT2's asymmetric shadow-ray fix is inappropriate here: DiRT3 already uses its
current inverse projection. GRID2's view-to-projector compensation supplies the
relevant matrix order, independently checked against this game's producer and
shader. Ordinary cascaded shadow constants are outside this change.

## Correction and ownership

For row-vector coordinates, the eye mapping is:

`M_eye = inverseView_eye * inverse(inverseView_producer) * M_producer`.

The observer records the native producer once per presentation. The scene consumes
that record once, validates renderer/context/owner, and supplies the original
baseline independently to each eye. Present, stop and scene destruction invalidate
the record. No native camera or shader buffer is changed by this correction.

Only the exact frontend shader pair, simple four-vertex draw and compatible
240-byte PS0 buffer qualify. A GPU-side comparison requires all 64 native matrix
bytes to equal the captured baseline; mismatch copies the original constants
unchanged. A private GPU buffer replaces only this matrix for the draw. D3D11.1
context-state swapping restores compute bindings; scoped restoration restores
the original PS0 buffer/range even if the draw callback throws. No GPU readback
occurs during normal rendering. Resources are released on VR shutdown.

The log's `guarded draw` records that the guarded GPU path was submitted. It does
not prove the GPU comparison matched or that the headset image is correct.

## Validation

Synthetic tests exercise nonidentity camera position/orientation, both eye
offsets, turning and leaning, fixed world-to-shadow coordinates, invalid matrix
refusal, same-frame consumption and interrupted scopes. WARP checks all three
shader variants, unrelated constant bytes, unchanged native buffers, stale-input
fallback, bindings, predication, UAV counters and callback exceptions. A separate
read-only contract test checks the installed executable bytes and actual shader
reflection offsets/sizes.

Headset acceptance remains pending. The next explicitly approved VDXR test must
check ground shadows while turning and leaning in the frontend, then verify
normal racing shadows, HUD proportions, intro completion and menu transitions.

The first approved 0.1.15 test reached the guarded draw path for both eyes, then
lost the graphics device during intro. NVIDIA event 153 and Present result
`0x887a0005` corroborate the failure. The game closed itself; no visual acceptance
was reached. Recovery restored exact originals. Added standalone tests render
600 real draws and validate output pixels; WARP and the explicit hardware/debug
run pass. The live failure remains unexplained; the next proposed comparison is
the previously accepted 0.1.14 package, requiring fresh launch approval.

The separately approved 0.1.14 control remained stable, confirmed by the user,
with 2,522 logged stereo submissions and clean shutdown. 0.1.16 replaces the
new shadow pass's `ClearState` with explicit unbinding of only its CS resources,
shader and constants, as in GRID2VR. All tests including standalone hardware
rendering pass. This narrowly tests a suspected state-reset interaction;
in-game stability is unverified and the cause is not yet confirmed. The next
launch needs a new approval.

The approved 0.1.16 retry (after a separately approved Steam restart) passed the
attended check: the user confirmed stability and that the yellow triangle
shadows now remain correctly placed. Native logs show both eyes reaching the
guarded path, 5,081 stereo submissions and clean shutdown without device errors.
Racing HUD was not retested; the user explicitly accepted proceeding. Exact
original files are restored. This validates the targeted frontend correction in
this session, not broader effects, SteamVR or sustained-driving coverage.
