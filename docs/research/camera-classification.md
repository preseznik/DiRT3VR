# DiRT 3 camera classification

Research date: 2026-10-28. Read-only stock-asset and focused native analysis; no game launch or installed-file modification. This identifies a candidate cockpit filter, **not a verified live-driving detector**.

## Target and reproducibility

The supported x86 `dirt3_game.exe` has SHA-256 `c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`, preferred image base `0x400000`. Addresses below are preferred virtual addresses; subtract `0x400000` for an RVA and add the runtime module base when resolving them.

`tools/research/inspect_cameras.py` reads ordinary XML and the observed little-endian BinXml chunk format, validates table bounds/tree membership and prints a read-only inventory. The format was checked against DiRT2VR's existing `.deps/Ego-Engine-Modding/src/EgoEngineLibrary/Xml/{XmlFile,BinaryXmlElement,BinaryXmlString}.cs`; these files describe the container, not DiRT 3 camera semantics. This target starts with bytes `1a 22 52 72`, not the older literal `BXML` signature.

Native evidence comes from REA/Ghidra 12.1.4, analysis-profile digest `b9d116ea480a02cc8773b19372074a3caf77785cbc080b1ac7133b5110e40b17`, with focused objdump instruction checks. REA evidence IDs below identify exact observations. The optional local analysis snapshot is `build/research/camera-classification.rea.json`; it is a research artifact, not a runtime dependency.

## Observed asset signatures

Stock `cars/models/frx/cameras.xml`: 61,136 bytes, SHA-256 `c8c989e557dd6729345a6baf564907e3e2fe4e2b07e9428d756fafbf4a981c58`, root `VehicleViewDefinition`, manager `frx`, 19 views.

| View | Type | nearPlane | FOV | highResInterior | DisableDriverHead |
| --- | --- | --- | --- | --- | --- |
| head-cam | Head | 0.0750 | 50 | true | true |
| head-cam_reverse | Mounted | 0.1 | 70 | false | false |
| bumper | Mounted | 0.1 | 60 | false | true |
| bonnet | Mounted | 0.1 | 60 | false | true |
| replay_driver1 / replay_driver2 | Mounted | 0.05 | 53.1 / 49.12 | true | false |
| replay_wheel1 / replay_wheel2 | Mounted | 0.0502 | 35.85 | false | true |
| replay_wing1 / replay_wing2 | Mounted | 0.05 | 40 | false | true |
| replay_hood1 / replay_hood2 | Mounted | 0.0502 | 34.82 / 36.4 | false | false |
| chase_close / chase_far | Chase | not explicit | 52 / 55 | not explicit | not explicit |

The stock cockpit has `hidden=false`, `spectator=false`, `reverseCamera=head-cam_reverse`, `postProcess=incar_frx`, `DisableDriver=false`, `DisableCoDriver=false`. Its position is `(0.3460, 1.0680, -0.3170)` with tilt `-2.75`; these are vehicle-specific calibration data, not global classification constants. Both replay-driver views are hidden, use `postProcess=incar_default` and explicitly enable the detailed interior. Therefore an interior flag alone accepts replay cameras.

A bounded inventory decoded all **63** installed `cars/models/*/cameras.xml` files without failure. Every exact `head-cam` was `Head`, near `0.0750`, detailed interior enabled and driver head disabled. No other view in those 63 files had a numeric near plane within `1e-6` of `0.075`. This is good supporting evidence for stock assets, but does not establish uniqueness across runtime-generated cameras or modified assets.

Other inspected stock assets:

| File | SHA-256 | Observation |
| --- | --- | --- |
| replay/replay_camera_system_data.xml | 2c7cacc81f929fad87569fab6c95942164a901c61c2009d93543acec5ce3d4d3 | Text XML; includes terminal-damage highlight and split-screen camera controls. |
| tracks/locations/frontend/frontend_track/route_0/replay_camera_config.xml | 8fb7f0fdeefc58360b18619ad11d4349203ae6b5d35282e57cb1766b832b2e38 | BinXml, 7 Camera elements, initial_camera, no named nearPlane parameter. |
| tracks/locations/usa/aspen/route_0/replay_camera_config.xml | 46925882f7084a1dcee0782630c7573cd44baccc9a51185c5d391a8df657a777 | BinXml, 39 Camera elements; explicitly activates target.replay_driver2; no named nearPlane parameter. |

The frontend track's replay configuration does **not** describe every frontend/menu camera. These observations do not justify calling near `0.2` a frontend signature. They also do not prove that the replay system can never display the ordinary head-cam.

## Native camera identity and producers

| Location | Observed behavior | REA evidence |
| --- | --- | --- |
| `0x9806d0` / RVA `0x5806d0` | Calls entry getter `0x975b50`, dereferences its first word, returns the selected camera object. | ev_494e1f5b5e973b945f0b72e3eaf2a3163f35895f06b037ff2cdaec6666a84894 |
| `0x975b50` / RVA `0x575b50` | Manager entry range is `+0/+4`, stride `0x280`, selected index `+0x140`, fallback entry `+0x2d0`. With argument false, reverse selection can substitute the reverse entry. | ev_18029eb6d0ecf221b5d41c04d93cba1d59a7f811827a7d1c1bdd587e2cb2451a |
| `0x53c0c0`, compare around `0x53c102` | Calls the selected-camera getter with true, adds `0xa1` to returned object and compares against `head-cam`, producing a head_cam label. | ev_11d1a27d853560dae0fe7f3cce7768e1a29e95b057868bd2b18a7d71e0ea86d7 |
| `0xb16690`, vtable write `0xb166ce` | Constructor installs vtable `0xf28530` (RVA `0xb28530`). Slot `+0x30` targets `0xb07ad0`, whose six-byte leaf returns the pointer to literal `Head` at `0xf285cc`. | ev_c25556f9f65971d52e4836b571f3b073ef2de061649a688655c5d3282e54a8f7; ev_f650d345de5f36ac6abb1a804ab81e22e0d7372e5fc8c7faeb6c3b63855fc9c1; ev_8fbdf7e5eb896ebbb5af97b45cedb5c8269dda3e21911d999b66fe4864edb147 |
| `0xb07cf0` | Head parameter loader places configured near at `+0x9c`, highResInterior at `+0x1a4f`, DisableDriverHead/Driver/CoDriver at `+0x1a50/+0x1a51/+0x1a52`; position at `+0x19d0`, base FOV `+0x19e4`, tilt `+0x1a24`. | ev_45602ed170e940ef4c8dab72fbee12a9dc41ef406099ae70cb2942367377cda4 |
| `0x972850` | Mounted loader has the same configured-near offset `+0x9c`, but different interior/head flag offsets `+0x19d5/+0x19d6`. | ev_550aded687ed2a924a5c76c1520cba3e4f04c4d1502ff515aee2e1af12b1c262 |
| `0x47c260` | Copies camera vectors at `+0x10/+0x20/+0x30/+0x40` and projection at `+0x50/+0x54/+0x58` to the **same destination offsets**; also copies remaining camera values through `+0x8c`, without the vtable or view name. | ev_20fa1d0761ff180f006dc581290af053120fa2c127c478062fdd554bf86ac8e3 |
| `0x576f40`, calls `0x576f4e/0x576f5e` | Copies global render-camera record `0xff7e50` (RVA `0xbf7e50`) into renderer inline records `+0x600/+0x690`. | ev_372c01a2733b3f203cd0f6c14d07a1f2a3a776c0303abcad70928650950534d7 |
| `0x577e10` | Multi-camera path iterates records with stride `0x90`; this is a reason to exclude split-screen initially. | ev_5e57465b780d5d776757b7839af716dffd77e455a2705b7d2a4bdf33be467196 |

The effective selected view must account for reverse mode. In `0x975b50`, argument false plus manager byte `+0x139` and object field `+0x118` can select the reverse index at object `+0x13c`. Argument true bypasses that substitution, so merely observing the label-producing caller above would incorrectly keep calling a rear view the cockpit.

`0x5aeb30` (xref at `0x5aec58`) references the global render-camera array and copies the controller-selected camera into a stack-local record before building view descriptors. Evidence: `ev_a964a0ffde9a206e13cb25230ab5edeeaa7dd78e256a351fa7a1fcd6766e0500`. The earlier description of this function as the global camera publisher was too strong: this observation does not establish its writer or the complete update order. Camera management in `0x5af320` accesses a manager through an owning object at `+0x11b4` then `+0x24`, calls selected-camera getters with both true and false, and compares named views; evidence `ev_8631c3c772049598dbd36470bef11aaa45cfa7f2174b7cc19c39e7df80c302ed`. These are candidate observation sites, not a proved live-camera-to-main-renderer chain. Ghidra warns about unreachable blocks in some large procedures; do not turn its inferred types or unidentified state integers into runtime facts.

## Candidate first cockpit gate

This was the initial cockpit-only plan. The user's later requirement for every
camera view supersedes its activation policy; see the 0.1.5 update below. Its
owner-label findings remain useful diagnostic evidence.

Use these checks together, with failure returning to an unmodified desktop/virtual-screen path:

1. The exact supported executable, verified main renderer, serial scheduling and a single primary view must already pass their independent guards.
2. Observe the game's effective selected-camera result at a proven producer/caller; preserve original call order and arguments. Record frame, owner/manager, exact bounded name, type and camera values. Do not invent new engine calls from the render hook.
3. Require exact name `head-cam` **and** the verified Head vtable. Only after validating that class/readable range, corroborate configured near `+0x9c ~= 0.075`, detailed interior enabled and driver head disabled. Reject reverse and Mounted classes, stale observations and ambiguous multiple owners.
4. Match the observed owner's finite camera pose/projection to the current global/main inline cameras at the correct point in the frame. The record uses the same offsets in DiRT 3; tolerances must come from paired observations, not assumptions. Never match just near or FOV.
5. Require an independently observed **live-driving, non-replay, non-frontend, non-loading** context. That state gate and the producer correlation in step 4 remain unverified. A name/type match alone is not enough to ship cockpit VR.

Next bounded observation should log the existing getters/producers without modifying cameras, capturing effective owner/name/class plus both main records and a candidate gameplay context during one normal cockpit drive, rear look, pause, replay and frontend transition. Confirm freshness and caller identity before implementing the filter. The automated benchmark uses moving cameras and cannot establish cockpit acceptance by itself.

## DiRT2VR reuse boundary

DiRT2VR `src/scene_camera.cpp::IdentifySceneCamera` (around line 172) combines photo/trailer/replay and live-driving gates with named-owner observations and camera matching. Reuse that separation of responsibilities and fail-closed behavior.

Do not copy its native constants: DiRT2's name offset `+0x80`, manager range `+4/+8`, entry stride `0x270`, selected index `+0x128` and getter/display RVAs differ from the DiRT 3 evidence above. DiRT2's comment about a four-float source/render adjustment is misleading: its float-array destination `data() + 4` is byte offset `+0x10`, matching its source offset. It does not justify shifting DiRT3 records. Its default cockpit near `0.075` happens to agree with these 63 stock assets; that agreement is corroboration, not proof of engine-wide behavioral identity.

## First interactive failure

The user-launched VDXR session for PID 33348 initialized OpenXR, submitted at
least 7,800 virtual-screen frames and submitted no stereo frames. The log records
`head-cam` with `display-mismatch` at 11:58:46.922; the user confirmed being in a
race and using the cockpit. The existing log cannot identify which copied field
differs or whether that difference persists after the camera transition.

Evidence is preserved in
`artifacts/diagnostics/interactive-20261028-pid33348/native-33348.log`, SHA-256
`73cee7bd407729d67fda7330450f4d13f137d5791feb77b4b00e5af932ce636c`.
Version 0.1.4 adds bounded field-level logging across owner-to-selected,
selected-to-global and global-to-main A/B comparisons. Acceptance remains exact;
no tolerance or near-plane-only fallback is justified by the current evidence.

## Confirmed mismatch and all-camera activation (0.1.5)

The next user-launched session, PID 31784, captured two cockpit samples one
second apart. Both report exactly one difference: owner `+0x58` is 2000
(`44fa0000`), while selected `+0x58` is 1000 (`447a0000`). Selected, global and
both main camera records match completely. Pose, FOV and near plane match across
the entire chain. VDXR initialized and submitted at least 7,500 screen frames,
with no stereo submissions. The process exited 0 and recovery completed.

Evidence: `artifacts/diagnostics/interactive-20261028-pid31784/native-31784.log`,
SHA-256 `190b8ab3f1d58b2eee31a0b0142becb821e795639749a2a3204e5b1fae0e9598`.
This establishes that source-to-render far clipping was incorrectly used as an
owner-identity condition. The owner label now excludes only far clipping from
that comparison; all displayed camera comparisons remain exact.

The user also requires every camera view in VR. Stereo eligibility now depends
on the verified main render boundary and its finite, matching global/main A/B
camera records, independently of owner metadata. Named driving cameras, reverse,
replay, alternate controllers and unnamed scenes use this same path. Renderer
and record identities, valid clip ranges, native stereo refusal, render-thread
ownership, history handling and partial-frame restoration remain enforced.
Two-dimensional frames with no eligible scene retain the virtual screen.

Synthetic regressions reproduce the exact far-distance difference and cover
all named view types, missing owner metadata, alternate controllers, invalid
renderer/record identities, non-finite values and mismatched displayed records.
The new binary still requires live visual, transition and lifecycle acceptance.
