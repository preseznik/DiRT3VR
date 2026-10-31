# Named launcher settings profiles — 0.1.18

Step 5 adds a Settings profiles section to the existing Settings tab. It supports
Save as new, Load, Update selected, Rename, Delete, Import, Export and Refresh.
The user accepted the desktop fullscreen Alt+Tab resolution change as a known
issue and requested this next batch on 2026-10-29.

Profiles include desktop resolution/mode/VSync, VR render scale/runtime choice,
experimental VR opt-in, appearance and diagnostic logging. Local runtime manifest
paths, game paths, detected headset dimensions, window placement, recovery data
and game saves are excluded. Loading retains the local runtime paths and
invalidates cached headset dimensions. Existing VR availability guards still
apply, including the disabled interactive SteamVR path.

Each installation owns `settings-profiles/<guid>.json` below its existing user
data directory. The original 0.1.18 document carries Kind=`DiRT3VR.SettingsProfile`, Schema=1,
Name and Settings. Names are 1–64 characters, trimmed and Unicode-normalized;
case-insensitive duplicates are refused. Names never become internal paths.
Version 0.1.19 exports Schema=2 with SkipStartupMovies included. Schema-1 files
remain readable with startup skipping off; updating or importing rewrites them
as schema 2. Older launchers refuse schema-2 files rather than dropping the choice.
At most 128 profiles are accepted, with a 64 KiB document limit. Unknown fields,
duplicate keys, foreign kinds, unsupported versions, invalid values, linked
paths and malformed JSON are refused. UTF-8 BOMs are accepted. Writes use the
existing atomic-file helper. Updates and deletion compare the selected file's
revision to refuse stale actions. Corrupt entries remain visible and cannot be
loaded or overwritten; other profiles remain usable.

Selecting or refreshing the list never applies settings. Load updates the
existing controls and keeps the selected tab. If current controls differ from
the saved preference baseline, replacing them requires confirmation. Cancel
keeps them untouched. Loading does not write preferences or game files. The user
can Save settings or Launch to persist the loaded choices. Save as new and Update
selected capture the current controls without silently persisting preferences.
Update and Delete require explicit UI confirmation. Rename uses the Name field
while retaining the saved settings. Closing retains the existing behavior:
window placement is remembered, unsaved settings are not automatically saved.

Import creates a new profile without loading it; name conflicts receive a
numbered suffix. Export writes the selected saved profile, not unsaved controls.
The file picker confirms destination replacement. Neither operation runs a game,
queries a headset, modifies game XML or mounts a game career. Installation
switches reload the profile list and ask before discarding unsaved controls.

Validation uses isolated fixture directories and actual WinForms button handlers.
It covers CRUD, stale revisions, per-install separation, all portable fields,
local-path preservation, failed imports, duplicate names, corruption isolation,
BOM input, cancelled loads, explicit persistence and Modern/Classic resizing at
560/900/1440 logical widths. Rendered screenshots use the host's 150% DPI; actual
100/200% DPI and other Windows theme settings are not claimed. The existing
launcher suite also checks VR guards, recovery and display controls.

The native DLL and x86 runtime probe are unchanged from 0.1.17/accepted 0.1.16.
No game launch is authorized or needed for this batch. Any later live launch
still requires fresh approval.

## Verified local delivery

- Portable package: `artifacts/packages/0.1.18-20261029-004823/DiRT3VR`.
- Both the final source run and the run against packaged capability data passed
  **735 launcher checks**. Modern and Classic screenshots were inspected at
  narrow and normal widths. No game process was started by any check.
- All **22 payload files**, native architectures, file hashes and ZIP contents
  passed `tools/verify_package.py`. The packaged executable's `--check-install`
  exited 0 and reported version 0.1.18.0.
- ZIP SHA-256: `bd6831cf950a3bbe6b716a9a9b53a920c62164818be4e73431429be73979eef7`.
- Native DLL SHA-256: `a50f3b7f3af99cd4e16250e6d78691f5c4793383c7bab166f36e59d644ff669b`.
- x86 probe SHA-256: `138cc17fde053f459f3f386458c38893802a02f6d4fa792da81dcc43843d2cb9`.
- Build/test/package logs and install-check results:
  `artifacts/diagnostics/settings-profiles-0.1.18-20261029`.
  NuGet vulnerability-feed checks were unavailable (NU1900); compilation and
  tests succeeded. No new package dependency was added.
- The accepted native runtime was preserved byte-for-byte, so native tests and
  headset driving were not repeated for this launcher-only batch. Nothing was
  published. The next planned item is startup conveniences (step 6).
