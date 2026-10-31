# DiRT 3 asymmetric projection and prepared visibility

This is an implementation contract for the installed x86 executable with SHA-256
`c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`.
All addresses in the evidence are preferred VAs at image base `0x400000`;
the implementation uses RVAs. These findings are from file disassembly, not
headset validation. No new REA session or game process was opened for this work.

The port can use DiRT2VR's two-hook approach: scope the main camera setup and
replace angular projection coefficients just before its D3D11 upload. There is
also a direct equivalent of DiRT2VR's earlier prepared-visibility copy hook.
The implementation is in `src/scene_projection.h/.cpp`; pure matrix and
visibility contracts are tested by `tests/native/scene_projection_test.cpp`.

## Concrete camera context

The main renderer's field `+0x5f4` points to a `CAMERANODE` object. Registration
at `0xde5b20` associates type token `0x10b4128`, string `0xf07764`
(`CAMERANODE`), and factory `0x8e2a50`. The factory allocates **0x1d0 bytes**
and calls constructor `0x8e1550`; the constructor installs vtable `0xf075e8`.
Its virtual method `+0x40` is the projection builder `0x8ab360`.

| Context bytes | Meaning and evidence |
| --- | --- |
| `+0x30..0x6f` | Duplicate of the camera world transform written from `+0x70` at `0x97b4e5..0x97b506` |
| `+0x70..0xaf` | Inverse view / camera world transform, constructed by inverting the view; independent of projection |
| `+0xe0` | Perspective mode byte for the context's generic projection builder |
| `+0xe4` | Camera FOV converted to degrees |
| `+0xe8` / `+0xec` | Near / far values; near comes from renderer `+0x40`, far is the setup argument |
| `+0xf0` | Target aspect ratio times renderer aspect factor `+0xb4` |
| `+0xf4..0x100` | Orthographic extents, not an asymmetric perspective interface |
| `+0x110..0x14f` | Projection matrix |
| `+0x150..0x18f` | View matrix |
| `+0x190..0x1cf` | Row-vector product `view * projection` |

`CameraSetup` is `void __thiscall(renderer, context, camera, float farPlane,
unsigned mode)` at VA `0x97a9b0`, with `ret 0x10`. **The last argument is a
bitfield, not a boolean**: bit 0 enables upload; bit 1 permits the native driver
stereo projection branch. Driver stereo remains disabled for this port.

The camera's `+0x70` byte selects an **orthographic** path. The corresponding
builder uses extents to produce affine scale/translation and a final `w=1`.
Setting this byte does not implement headset perspective. The normal perspective
and native stereo perspective are built in local storage inside CameraSetup,
then written to `+0x110` at `0x97b537..0x97b550`. Hooking the generic context
projection builder alone would be overwritten later in CameraSetup.

## Upload boundary and dependent state

The renderer field `+0x28` points to the D3D11 device object. Constructor
`0x8d8bd0` installs vtable `0xf06ab8`; its method `+0x148` is
`void __thiscall(device, context)` at **VA `0x8a81f0`**, with `ret 4`.
CameraSetup calls it at `0x97b63d` and returns to `0x97b63f`.

At upload entry, view and projection are complete. Update four coefficients:

```text
l = tan(fov.angleLeft); r = tan(fov.angleRight)
d = tan(fov.angleDown); u = tan(fov.angleUp)
P[0] = 2/(r-l); P[5] = 2/(u-d)
P[8] = (r+l)/(r-l); P[9] = (u+d)/(u-d)
VP = V * P
```

Preserve `P[10]`, `P[11]`, `P[14]`, `P[15]` and every other native coefficient.
Require the verified right-handed perspective convention `P[11]=-1, P[15]=0`,
finite inputs, valid FOV angles, and a finite product before writing either
output. This accommodates the main foreground pass's different near/far values.
The port does not replace the engine's clip-depth convention with an assumed
OpenXR depth convention.

The original upload maps the constant buffer stored at device `+0xb30` through
the D3D11 immediate context at device `+0x14`, using map mode 4. It copies:

- projection into constant-buffer `+0x00..0x3f`;
- view-projection into `+0x40..0x7f`;
- view and inverse-view components into subsequent fields;
- an inverse calculated from the **current projection**, into `+0xf0..0x12f`.

It then unmaps and binds the buffer at shader slot 3. Consequently, changing P
and VP **before** calling the original upload also refreshes its derived inverse
projection. `context+0x70` is not an inverse projection and must remain untouched.
No visibility volume is rebuilt in this upload; that occurs earlier.

The scoped hook must cover every CameraSetup call within the main eye pass whose
renderer, context and camera identities match the supplied main eye scope.
The two calls from `0x984a8b` and `0x984adf` establish depth and colour views,
but they are not the only calls. The foreground pass calls setup at `0x58071d`
with changed near/far values and then renders list roots `+0x8c/+0x90/+0xdc`.
Restricting the setup hook to the two direct inner callsites could leave that
geometry using the original projection. The upload hook additionally requires
return RVA `0x57b63f`, exact device vtable and the scope's context pointer.

`ProjectionScope` restores its previous thread-local scope on destruction.
It leaves the generated context matrices in place through the eye, just like
DiRT2VR; the next native CameraSetup regenerates them. It does not copy or rewind
opaque object state. Its `Valid()` must remain true and `Uploads()` must be
nonzero before treating an eye as successfully rendered.

### CPU context restoration audit

The setup's observed camera-node writes cover `+0x30..0xaf`, mode byte `+0xe0`,
the four scalar fields `+0xe4..0xf3`, and matrices `+0x110..0x1cf`; its
orthographic branch additionally writes extents `+0xf4..0x103`. The native
projection-builder calls write the projection matrix. CameraUpload then
persists a separate GPU constant buffer and binds it to shader stages. The
outer routine returns without resetting the node; its main caller merely
releases the prepared scene at `0x580650..0x580666`. DiRT2VR likewise retains
the last generated node state rather than restoring a CPU snapshot.

Restoring only P/VP, or even every known CPU node field, would leave that GPU
constant buffer describing another view. Re-uploading a snapshot would also
change shader bindings and roll back the final foreground projection. Neither
operation is established as the native postpass contract. Accordingly no blind
camera-node rollback was added. Later screen/HUD consumers and partial-failure
transitions still need attended validation; retaining the native setup sequence
is not proof of their correctness.

There is a narrower, verified shared input to restore per eye: renderer float
`+0x40`, the near clip. The foreground pass writes **0.05** there at `0x580707`
and calls CameraSetup at `0x58071d` with far **5.0**, without restoring `+0x40`.
The second eye's ordinary depth/colour setup must start with the same near input
as the first eye. DiRT2VR already wraps every inner replay with
`ScopedSceneDepth(renderer+0x40)`. The runtime integration was sent this exact
restoration requirement, including partial-failure cleanup. No extra scene,
simulation, list, history or serial replay is needed for it.

## Visibility before scene preparation

Main preparation at **VA `0x5808b0`** has the same sequence as the corresponding
DiRT2VR preparation path:

1. Call CameraSetup at `0x580914`, with main camera A (`renderer+0x600`), main
   context `*(renderer+0x5f4)`, and mode 0 (no upload).
2. Pass `context+0x190` to **frustum builder `0x7c55d0`** at `0x580932`.
3. Copy its result with **`0x574c10`** at `0x58093a`, returning to
   **RVA `0x18093f`**, into `renderer+0x350`.
4. Fill additional native position, FOV, distance and preparation fields before
   submitting the prepared scene.

Both builder and copy use `void* __thiscall(destination, const void* source)`
and return the destination in EAX with `ret 4`. The builder inverts the supplied
4x4 matrix, transforms eight clip-space corners into eight padded XYZ records
at `+0x60..0xdf`, then constructs six planes at `+0x00..0x5f`. The copy transfers
exactly **0xe0 bytes / 56 floats**. The corner fourth lanes are preserved padding;
zero-initialize a newly built output volume before passing it to the builder.

The implemented copy hook recognizes only that exact return address and the
exact main renderer/camera/context identities. It builds a finite orthographic
box for **visibility only** using DiRT2VR's algorithm:

- Center it halfway between the original A/B camera positions (`+0x40`).
- Start with the larger camera far value (`+0x58`, accepted range 25..5000).
- Enclose the radius of every original native corner; reject nonfinite radii or
  a radius above 100000. This includes native track-specific distance changes.
- Add 8 units of translation margin and half the A/B separation on each axis.
- Use diagonal scales `(1,1,-1)/extent`, corresponding translation, and `m[15]=1`.
- Run the native builder on aligned temporary storage; call the original copy
  only when all 56 output floats are finite. Otherwise copy the original volume.

This preserves native distance/LOD fields and widens only the main directional
visibility volume. Other occlusion systems, shadow frustums, reflections and
effect-specific visibility still need live acceptance. The hook's wider workload
also requires performance measurement. It must not be presented as proof of
complete scenery or lighting coverage.

## Guards and reproduction

`InstallSceneProjection` must be called only after the production executable hash
guard succeeds. It additionally requires `DIRT3VR_XR_PROTOTYPE=1`, checks loaded
entry bytes, device/camera-node vtable slots, the exact upload instruction block,
and the relevant direct call instructions before installing the three hooks.
Installation now starts **inactive**. Only after all presentation guards succeed
may the caller use `SetSceneProjectionActive(true)`; XR stop/failure must call it
with false. Both prepared-visibility expansion and eye projection changes check
this independent activation state. A failed classifier or exit-hook setup can
therefore leave the installed pass-through hooks without changing visibility.
Eye projection additionally requires an explicit valid ProjectionScope.

Before any eye pair, `NativeStereoDisabled(renderer)` verifies the main renderer
and its device at `+0x28`, device vtable `0xf06ab8`, its `+0xd0` slot targeting
`0x8a8ca0`, and all **61 bytes** of that getter. The getter returns true for
signed device DWORD `+0x4d60 > 0`; otherwise it may query a vendor stereo handle
at `+0x4d78`. The read-only guard therefore requires `+0x4d60 <= 0` and a null
`+0x4d78`, and never calls the vendor API. A non-null handle is conservatively
refused even if the driver's eventual query could have returned false.

This guard is necessary independently of the XML setting: outer code at
`0x98a388..0x98a432` can issue its own second inner call when renderer `+0x14`
is `0x400/0x404` and that getter returns true. Without refusal, the native second
call could become a third render after the mod pair and overwrite eye-1 history.
CameraSetup also consults the getter at `0x97aef8` for driver projection.

| Routine | RVA | Size in manifest | SHA-256 of complete range |
| --- | --- | --- | --- |
| CameraSetup | `0x57a9b0` | `0xc9a` | `3c362b5650e0a508774d34e6f6e4de4ff464dc605d6227e0cc7585af58518469` |
| CameraUpload | `0x4a81f0` | `0x731` | `0cb336e24bc5e6e368d8aac345843b4be7e5c3d04f4e99eadaa89867e8509f69` |
| FrustumBuild | `0x3c55d0` | `0x669` | `5d16b53071bdc876ec09b8051f55dff90daa0f8ab5c4bac49e5adedac2d43f7a` |
| FrustumCopy | `0x174c10` | `0x9e` | `5aeb5a825717aaa70a2cd0b8637ad75b9a81d1607a2ba3dafa98c6382acd5184` |

The first three share the generic 16-byte aligned prologue
`53 8b dc 83 ec 08 83 e4 f0 83 c4 04 55 8b 6b 04`. It is not a unique signature
and must never substitute for the executable fingerprint and address checks.
FrustumCopy begins `55 8b ec 8b c1 8b 4d 08 0f 28 01 0f 29 00 0f 28`.

Reproduce the checked evidence with `tools/research/inspect_render_boundary.py`.
It emits 55 disassembly ranges, 14 direct-call checks, vtables and strings in
`docs/research/static-evidence/`. No code from the target is executed.

Focused MSVC Win32 `/W4 /EHsc` compilation and native tests passed. Tests cover
translated world points reaching asymmetric eye edges, native depth preservation,
orthographic refusal, failure without partial output changes, enclosing distant
native visibility corners, ignoring corner padding, rejecting invalid far/radius
values, refusing activation without successful guarded installation, and a
synthetic loaded device's native-stereo count/handle/type/instruction guards. These are
offline contracts; headset image quality and gameplay are separate acceptance.
