# Startup edits and multiplayer (#5f45bd3f)

DiRT2VR 0.7.2 found that a modified `states.bin` failed DiRT 2's multiplayer race-loading
validation and caused disconnects, so it keeps the original file for host and join. DiRT3VR's
startup skip edits `system/states.bin` too (logo movies today; copyright screen and Press Start
camera flight with `tools/startup_skip.py`). Static findings for DiRT 3 (fingerprint `c502b495…`,
2026-11-03):

- DiRT 3 has a network data-checksum system: `game::NetChecksumDataValidationInterface`,
  `neRacing::IChecksumManager`, `neRacing::IDataChecksumMonitor`, the states
  `StatePerformChecksumTest` (`checksum_test`) and `StatePauseChecksuming`
  (`pause_car_type_cs`, item `CarType`).
- Its configuration is loaded from `system/boot_data.xml` as two `CSManager` files,
  `network/csdata.xml` and `network/csconfig.xml`. The PC install has no `network` folder, and a
  file trace of a start (run 20261101-080613) shows the game opening `boot_data.xml`, `flow.bin`,
  `boot_preload.xml`, `durations.xml`, `states.bin` and `links.bin`, but neither checksum file.
  (`boot_data_steam.xml` / `states_steam.bin` exist but aren't loaded.)
- The only item the shipped states name for checksumming is `CarType` (car data), not the
  front-end flow or states.

So nothing points at `states.bin` being part of DiRT 3's multiplayer check on PC, unlike DiRT 2,
but that is an inference from missing files and names, not a test. Only a real join settles it:
host a multiplayer race on one PC with the startup skip on and join it from another without (and
the other way round), then finish a race. Until then the launcher's advice stands: turn the skip
off for multiplayer if a race fails to load or disconnects.
