# Bloom off

Wanted (#3c6d6ffa): a launcher option to turn DiRT 3's bloom off. The game has no bloom
setting ([graphics options](graphics-options.md)), so the earlier note expected renderer
work. It isn't needed: bloom strength is data in the post-processing effect files, the
same files the motion blur edit uses ([motion blur](motion-blur.md)), and DiRT2VR's bloom
option zeroes the same group.

## The data

Each `Effect` may have a `ParameterGroup name="BloomFromDsX4"`, by its name a bright pass
on a 4× downsampled copy of the frame, blurred at several sizes and added back. Its
`lowerThreshold` picks the bright pixels; `mixMedium`, `mixLarge`, `mixHuge` and
`mixStreak` weight each blurred level added back. With all four at 0 nothing should be
added, whatever the threshold.

| File | Format | Effects with BloomFromDsX4 | Non-zero |
| --- | --- | --- | --- |
| `postprocess\effects.xml` | Ego binary XML | 61 of 156 | 60 |
| `tracks\generic\effects.xml` | text XML | 1 | 1 (mixMedium 0.0694) |
| `tracks\generic\night_effects.xml` | Ego binary XML | 1 | 1 (all four mixes) |

In `postprocess\effects.xml`, `race_base` already has zero mixes; the 60 with bloom are 54
per-track effects (`t~<location>~<route>[~day_alt|~night|~wet|~snowing]`, the frontend
track among them) plus `finish_fx_green`, `FlashbackFX_01`, `outbreak` and the three
message-box effects. In the supported install mixMedium runs 0.0694–1, mixLarge
0.1584–1.5954, mixHuge 0.185–1.9998 (most tracks use 1.9998), and only 6 effects
set mixStreak (0.0289–0.0665). `ColourBalance`'s `bloomPrevalence` is a colour-grading
input, not the bloom passes, and stays.

No other game XML has bloom mixes: a scan of every `.xml` for `BloomFromDsX4`, `mixHuge`,
`bloomAmount` and `FinalMix` finds only these three files. `frontend\persistentdatarender.xml`,
which DiRT2VR also patches, has no bloom parameters in DiRT 3. `postprocess\type*.xml` name
"bloom" often, but they define the pass chains that effects pick with `chain=`, not strengths.

## Edit and launcher contract

`tools/bloom.py` `disable_bloom(data)` sets every BloomFromDsX4 mix to `0.000000` and
leaves the thresholds and other groups alone. For the binary files it uses the motion blur
rule (new strings appended, only those `Param` elements repointed; every other byte as
shipped). For the text file it replaces only the numbers inside the BloomFromDsX4 mix
`Param`s, keeping whitespace and CRLF line endings; the file keeps its length. Zero mixes
keep their bytes, applying it twice gives the same bytes, and a file without bloom mixes
is refused. On the installed files it changes exactly the non-zero mixes
(`tests/test_bloom.py`, which also runs on the installed files when present).
`python tools/bloom.py <file>` lists the mixes.

For the launcher, like motion blur: journal each of the three files as its own target,
write `disable_bloom(original)` before the game starts when the option is on, restore the
originals afterwards, and let recovery and the elevated worker accept only that exact
transform of the recorded original. `postprocess\effects.xml` is shared with the VR motion
blur edit, so a VR session with bloom off writes `disable_bloom(disable_motion_blur(original))`.
The two edits touch different elements, and on the installed file either order gives the
same bytes.

## In the game (2026-11-03)

`run_benchmark.py --no-bloom` applies the edit for one journaled run. DiRT 3 reads
`postprocess\effects.xml` from disk at startup (file trace, runs of 2026-11-01).

The benchmark's timeline moves with loading time (0.4–3 s between runs), so the comparison
used 16 screenshots one second apart while the car drives (Aspen route_0, frx, 1280×720), paired by
the HUD lap timer. The bloom-off run and a bloom-on run (the 16× anisotropic run) lined up within
6–60 ms. Between them:

- No glow structure. The low-frequency difference (12 px blur) averages −2 to +1 levels (of 255) per
  frame, and its only local differences are the car's snow spray, not halos around bright snow or sky.
- Dark and mid levels are the same. Medians per frame agree within 1–3 levels (164/164, 171/172,
  167/169…). An earlier 11-level gap against another baseline was its 0.43 s timing offset.

So in Aspen's daytime driving, BloomFromDsX4 adds almost nothing: Aspen's lowerThreshold is 0.7425
with mediumBloomThreshold 1.2177, and little of the frame passes them. The strong sun glare in
the pre-race views comes from two other groups of the same effect, untouched by this edit and
still present with bloom off:

- `Streaks` (multiplier_0..2 = 1.27, 1.16, 4.0, threshold 0.75; blue) — the horizontal blue streak
  through the sun.
- `LensFlares` (`multiplierAndThreshold_*`, `multiplier` 2.0, `threshold` 0.1) — the flare chain.

Both are screen-space effects; in VR each eye would draw its own flare and streak position, which
is the likely "sun bloom" complaint from DiRT2VR. iztok chose two options (2026-11-03): bloom, and
streaks + flares.

## Light streaks and lens flares off

`tools/glare.py` `disable_glare(data)` zeroes the Streaks `multiplier_0..2` (50 effects, race_base
among them at 0.4 / 0.4 / 1.5) and the LensFlares `multiplier` (61 effects, 55 non-zero) in
`postprocess\effects.xml`, by the same appended-string rule; thresholds, colours, `upperLimit`
and the per-element `multiplierAndThreshold_*` vectors stay. race_base's `upperLimit` is 0
with non-zero multipliers, so `upperLimit` 0 alone doesn't mean "off". The generic track files
have neither group. Bloom and glare edits touch different elements and give the same bytes in
either order (`tests/test_glare.py`). `run_benchmark.py --no-glare` (alone or with `--no-bloom`)
applies it for one journaled run. For the launcher: a second option, journaling the same
`postprocess\effects.xml` target as bloom and motion blur and writing whichever edits are on.
Not yet seen in the game: a run with the sun in view settles whether `multiplier` alone turns
the flare chain off. Night tracks, finish and flashback effects (higher mixes, e.g.
finish_fx_green mixLarge 1.5954) weren't run.
