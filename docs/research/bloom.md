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

Not yet seen in the game. Expected: no glow around bright sky, snow, headlights and
the finish effects; the next step is a screenshot pair of the same race start with and
without the edit.
