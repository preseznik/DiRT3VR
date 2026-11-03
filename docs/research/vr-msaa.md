# VR anti-aliasing (MSAA)

Wanted (#c8076a1e): DiRT 3's own MSAA in VR, as DiRT2VR offers it (default 2×). VR sessions
write `multisampling="off"` (`launcher/ExperimentalFiles.vb`), and the earlier note in
[graphics options](graphics-options.md) expected renderer work. The question was whether
DiRT3VR's stereo path survives MSAA: each eye is copied from the game's backbuffer after its
inner scene call (`RenderVrScene` → `TexturePresenter::Draw`), and the HUD capture, the render
probe and the scene-resolution contract all require a single-sample backbuffer.

## Runs

2026-11-03 on PC4080S, d623007's proxy, Aspen benchmark at 1280×720 windowed:
`run_benchmark.py --run --scene-observer --serial-render --replay-pair --msaa <level> --seconds 240`.

| Level | Run | Level in the game's result | Swap chain | Replay pairs | Sampled scenes |
| --- | --- | --- | --- | --- | --- |
| off (baseline) | 20261028-104835-08735836 | off | 1 sample | 120 of 120 | 792, 852, 911 |
| 2xmsaa | 20261103-183038-65b84c9f | 2xmsaa | 1 sample | 120 of 120 | 791, 851, 910 |
| 4xmsaa | 20261103-183306-b739a99c | 4xmsaa | 1 sample | 120 of 120 | 14113, 14173, 14232 |

Both MSAA runs ended normally with `requestedConfigurationVerified`, the replay evidence check
passed, and every protected file was restored unchanged.

## Findings

- With MSAA on, DiRT 3 still creates a single-sample swap chain and resolves before the
  backbuffer, as DiRT2VR's notes describe for DiRT 2 (2× MSAA HDR scene targets, single-sample
  backbuffer). The eye copy, HUD capture, render probe and scene-resolution contract therefore
  see the same kind of backbuffer as with MSAA off.
- The eye images show the effect: the Aspen lodge roof against the sky has stair steps with MSAA
  off and blended edge pixels at 2×.
- The XR swapchains stay single-sample, so the 512 MiB XR colour limit is unaffected.
- No renderer change is needed for `off`, `2xmsaa` and `4xmsaa`. The launcher writes the chosen
  level instead of `off`.

## Not covered

- `8xmsaa` was not run.
- Headset sizes: the runs are 1280×720. At a VR size such as 3024×3360 the game's multisampled
  targets grow with the sample count, so GPU memory and frame time need a headset session
  (DiRT2VR warns at 4× and 8×).
- Menus, transitions and the HUD in the headset.
