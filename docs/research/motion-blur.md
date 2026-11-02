# Motion blur in VR

Reported (#30d34783): before a race at LA Coliseum, in the low pre-race view of the car,
the ground nearest the view is garbled and stretched in the headset.

## Cause

DiRT 3's post-processing applies camera motion blur from a velocity buffer. In VR every
head movement is camera movement, added to the pre-race camera's own slow move. Motion
blur stretches each pixel along its screen motion, and that motion is largest for the
geometry nearest the camera; with a camera close to the ground, the nearby ground turns
into horizontal streaks. DiRT2VR met the same problem and disables motion blur for VR
sessions (`launcher/Transactions.vb`: every `MotionBlur` `blurLength` becomes 0 in
`postprocess\effects.xml`), with an additional renderer correction for its menus.
DIRT3VR did not touch motion blur before this research.

## The file

`<game>\postprocess\effects.xml` is Ego binary XML (265 KB; `tools/seat_cameras.py`
decodes and re-encodes it byte for byte). The root `PostProcessEffects` holds 156
`Effect` elements: `race_base`, per-track `t~<location>~<route>[~night]`, frontend,
flashback and finish effects. Some have `ParameterGroup name="MotionBlur"` with
`Param name="blurLength"` and `frozenBlurLength`; each value is the element's text,
e.g. `"\r\n\t\t\t6.000000\r\n\t\t"`. In the supported install:

| Effect | blurLength | frozenBlurLength |
| --- | --- | --- |
| race_base | 6 | 0.25 |
| t~frontend~frontend_track | 10.3584 | 1 |
| t~london~battersea, ~dirt3_night_test | 9.9884 | |
| t~london~battersea~night, ~dirt3_night_test~night | 10 | |
| finish_fx_green, FlashbackFX_01 | 7.0289 | |
| outbreak | 7.0289 | 1 |
| t~material_testbed~material_testbed_1 | 20 | |

LA Coliseum has no MotionBlur group of its own, so it races with `race_base`'s 6.

## Edit and launcher contract

`tools/motion_blur.py` `disable_motion_blur(data)` sets every MotionBlur `blurLength`
and `frozenBlurLength` to `0.000000`, keeping each value's surrounding whitespace. New
value strings are appended and only those `Param` elements are repointed (the seat edit
rule), so all other bytes stay as shipped; applying it twice gives the same bytes, and a
file without any `blurLength` is refused. On the installed file it changes exactly the
13 non-zero length elements (`tests/test_motion_blur.py`, which also runs on the
installed file when present). `python tools/motion_blur.py <effects.xml>` lists the
lengths.

For the launcher (VR sessions only, like the seat cameras): journal
`postprocess\effects.xml` as its own target, write `disable_motion_blur(original)`
before the game starts, restore the original afterwards, and let recovery and the
elevated worker accept only that exact transform of the recorded original. Desktop
play keeps the game's motion blur.

Not yet tried in the headset. Expected: no smearing of nearby geometry when the head
moves, in the pre-race view, menus and races alike.
