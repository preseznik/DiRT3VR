# Startup skip beyond the logo movies (#0ed1f030)

iztok asked to skip the copyright screen and the Press Start camera flight too. `tools/startup_skip.py`
turns `system/states.bin` nodes into `StateWayPoint` nodes, the rule the launcher's logo-movie skip
uses (`launcher/StartupMovies.vb`; its "movies" part is byte-identical to that edit).

Normal starts on PC4080S, 2026-11-03 (`run_benchmark.py --normal-start --skip-startup ...`, every
file restored):

| Skipped | Result | Run |
| --- | --- | --- |
| movies (today's launcher option) | loading ~3 s, copyright screen ~6 s, letterboxed camera flight over the DiRT 3 logo ~9 s, "PRESS START OR ENTER" from ~12 s | 20261103-220856 |
| movies + copyright screen + flight | crash after loading (0xC0000005, ~15 s) | 20261103-221042 |
| movies + copyright screen | same crash | 20261103-230706 |
| movies + flight | no crash, but black from ~9 s on; Press Start never shows | 20261103-230726 |

So `ScreenInitialLegal` (a screen) can't become a waypoint, and the camera flight
(`StateGarageZone press_start_cutscene`) sets up the garage view that Press Start draws on. Both
stay as they are; the launcher keeps skipping only the logo movies.

Next lead, data only and keeping every state: `frontend/durations.xml` (text XML, loaded at boot)
has `intro_part_1` (4 s) and `intro_part_2` (3 s), about the flight's length, and
`legal_fade_on` / `legal_fade_off` (0.3 s each). Shortening the intro parts may make the flight
quicker; untested.
