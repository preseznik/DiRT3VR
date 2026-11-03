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

## Cost

iztok: "measure it". DiRT 3's own Aspen benchmark on PC4080S (RTX 4080 SUPER), one view at
3840×2160, no diagnostics: `run_benchmark.py --run --msaa <level> --size 3840x2160`. DiRT 3 runs
only the sizes in `hardware_settings_info.xml`, so a headset's 3072×3264 can't be asked for
directly (it falls back to 1280×720); 3840×2160 is 8.3 MP against 10.0 MP for that eye.

| Level | Run | Average fps | Frame time | Minimum fps |
| --- | --- | --- | --- | --- |
| off | 20261103-202035-a128c1ec | 411.1 | 2.43 ms | 330.6 |
| 2xmsaa | 20261103-201555-072922b5 | 384.5 | 2.60 ms (+0.17) | 333.4 |
| 4xmsaa | 20261103-201757-679df5d4 | 363.8 | 2.75 ms (+0.32) | 313.9 |

Every run's result confirms its level and size, and every protected file was restored. The first
MSAA-off run (20261103-201351-cb8d24e1) had the window take focus for a moment and lose it, after
which DiRT 3 throttles to about 19 fps; it was repeated with `--background-play`. The 2× and 4×
runs never had focus and ran unthrottled.

So 2× costs about 7% of the frame rate and 4× about 12%. At 400 fps the benchmark may be partly
limited by the CPU, so the GPU cost can be somewhat higher than these differences. Scaled to a
headset pair (two eyes of 10 MP, 2.4 times these pixels), that is roughly 0.4 ms per frame at 2×
and 0.8 ms at 4×, out of 11.1 ms at 90 Hz. That's an estimate; a headset session's
performance-summary.json would measure it.

## Not covered

- `8xmsaa` was not run.
- GPU memory and frame time in a headset session at the headset's own size (DiRT2VR warns at 4×
  and 8×).
- Menus, transitions and the HUD in the headset.
