# Read-only displayed-camera classifier

Implemented 2026-10-28 in `src/camera_classifier.{h,cpp}`. This extends the
[asset/class evidence](camera-classification.md) with a concrete ownership
chain. It is an experimental camera identity gate, not completed gameplay,
pause or replay acceptance. No game was launched to validate this classifier.

## Read-only ownership and display checks

The first primary camera controller is reached through `*(0xfe7404)`, then
pointer `+8`, pointer `+0x11b4`, and its manager at `+0x24`. The publisher's
instructions at `0x5aecec..0x5aed11` read the same root and controller, call
`0xb03ba0`, and copy the selected camera with `0x47c260`. The current primary
controller's camera is `+0x150` only when bytes `+0x11/+0x12` are both zero and
the manager exists. Its other branches return `+0x30` or `+0xc0`; those branches
are deliberately rejected because their gameplay meanings remain unresolved.

The implementation reproduces the read-only selection in `0x975b50(false)`:
manager entries `+0/+4`, stride `0x280`, selected index `+0x140`, reverse byte
`+0x139`, and selected object's reverse availability/index at `+0x118/+0x13c`.
Both ordinary and reverse indices must be in a bounded, aligned entry range.
The native inline fallback `manager+0x2d0` is not accepted because its provenance
is unknown. No engine function is called and no hook is added.

All XYZ components at `+0x10/+0x20/+0x30/+0x40`, and FOV/near/far at
`+0x50/+0x54/+0x58`, must be finite and **bit-identical** between the effective
selected object, controller record `+0x150`, global record `0xff7e50`, and both
main-renderer records `+0x600/+0x690`. W/padding, history bytes and vtables are
not interpreted as pose. The main renderer and inline camera vtables are
independently checked. Selection and source data are read again to reject a
changing identity; no cached positive result survives a failed current read.
This is a conservative observation, not synchronization with another thread.
Serial render scheduling remains a separate prerequisite.

`Cockpit` additionally requires exact bounded name `head-cam`, Head vtable
`0xf28530`, configured near `+0x9c` and current near `+0x54` within `1e-6` of
`0.075`, detailed interior byte `+0x1a4f`, and disabled-head byte `+0x1a50`.
Matched names `chase_close`, `chase_far`, `bonnet`, and `bumper` return `External`;
this identifies their displayed named owner but does not assert its native
class or gameplay state. Other names, reverse views, mismatches, malformed
memory and unavailable guards return `Screen` with a diagnostic reason.

## Integration contract and limitations

After the existing executable SHA-256 guard, call
`InitializeCameraClassifier(logger)`. It checks loaded getter/selector bytes,
publisher/copy call targets, relocated global references and the Head type
vtable leaf. Call `ClassifyMainCamera(renderer, cameraA, cameraB)` at the main
inner boundary **before eye poses change either camera**. Diagnostics log only
when identity or its refusal reason changes, never continuously for movement.

The result's `gameplay`, `paused`, and `replay` fields always remain `Unknown`.
The parent implementation may allow positively matched `Cockpit` in the
explicit interactive prototype, but must describe replay/pause behavior as
unverified. A replay can potentially display the ordinary Head camera; current
evidence does not exclude it. External benchmark replay remains a separate
diagnostic override. Do not promote this to a completed live-driving detector.

Exact record matching may reject a valid cockpit if the engine legitimately
changes FOV, pose or another matched field between these producers. There is no
guessed tolerance or near-plane fallback. A future user-approved attended run
can establish whether current records match and reveal the smallest required
producer observation. All game launches require fresh explicit user approval.

## Static validation

`tests/native/camera_classifier_test.cpp` checks a synthetic sparse address space:
matching Head, unknown context fields, missing guards, type disagreement,
distinct pose/FOV, non-finite values, reverse lookup and invalid reverse index,
fallback rejection, malformed entry range, alternate controller paths, changing
source, bounded names, supported external names and replay-name refusal.
MSVC Win32 C++20 `/W4` compilation and all assertions passed independently.

`tools/research/verify_camera_classifier.py` checks the supported executable
fingerprint and every runtime instruction/address guard without loading or
executing the game. Its local run passed. These checks establish implementation
and binary consistency only; no positive live cockpit classification is claimed.
