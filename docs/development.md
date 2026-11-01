# Development

Requirements: Windows x64, Visual Studio C++ x86 tools with CMake/Ninja,
Python 3, Git, .NET 10 SDK and Inno Setup 7 (`ISCC.exe`, default
`%LOCALAPPDATA%\Programs\Inno Setup 7`, or pass `-InnoCompiler`). Native outputs
target x86; the launcher targets x64. The launcher's controller reader
(`tools/driving-input`, from GRID2VR; see its `source.json`) is a separate x64
DLL: `tools/build.ps1` builds it with `vcvars64.bat` and Ninja into
`build/driving-input`, runs its `driving_axes` test, and the launcher tests copy
it beside themselves when present. Packages ship it as `payload/driving_input.dll`.

`tools/package.ps1` writes the portable folder, its ZIP, the
`DiRT3VR-<version>-Setup.exe` installer built from `installer/DiRT3VR.iss` and
`SHA256SUMS.txt`; `python tools/verify_package.py <package>/DiRT3VR` checks all of
them. Setup installs the portable layout into `<game>\DiRT3VR`, finds the game
with `DiRT3VR.exe --discover`, and runs `--check-install` / `--check-uninstall`
(supported executable, DiRT 3 closed, no pending recovery) before changing files.

The badge artwork is `launcher/assets/DiRT3VR.png`. To replace it, run
`python tools/prepare-launcher-art.py <artwork.png>` (cuts the badge out of a black
background) and `tools/make-launcher-icon.ps1` (16–256 px `DiRT3VR.ico`). The icon
is embedded in the executable and as a resource for launcher windows; Setup uses
the same ICO.

Launcher sections must be collapsible. Create each one with
`LauncherLayout.Section`, which adds a `SectionHeader` (▾/▸, click or Space/Enter);
MainForm stores collapsed titles in `window-preferences.json`. The launcher tests
fail if a section starts with a plain bold label instead.

```powershell
./tools/bootstrap.ps1
./tools/build.ps1
./tools/package.ps1 -SkipBuild
```

For an offline bootstrap, use `-SourceRoot <directory containing minhook and
OpenXR-SDK checkouts>`. Only clean, exactly pinned tracked sources are exported.
Existing source receipts are verified rather than overwritten. The dependency
folders are real copies, not links to another mod project.

`tools/build.ps1 -NativeOnly` builds the native components and runs CTest.
Additional runner recovery checks: `python -m unittest discover -s tests -p 'test_*.py' -v`.
Some Windows sandbox configurations deny MSVC debug helpers or atomic file
replacement even under temporary directories; run those checks in the authorized
normal build environment without weakening the implementation's checks.

## Releases and update checks

The launcher's update check (`launcher/Updates.vb`, adapted from DiRT2VR) reads
the GitHub Releases of `preseznik/DiRT3VR`, so it finds nothing until the
repository is public; a 404 is shown as "no public releases yet" and the startup
check stays silent. Only installed packages (with `package.json`) check at
startup; development and test builds check only when asked.

For a release to be offered:

- Tag it `vX.Y.Z` matching `<Version>` in `launcher/DiRT3VR.vbproj`. Mark
  experimental releases as a GitHub prerelease, or use a prerelease version such
  as `0.2.0-beta.1`; those are offered only to players who opt in.
- Attach `DiRT3VR-X.Y.Z-Setup.exe` from `tools/package.ps1` unchanged. The
  launcher accepts only that exact asset URL in this repository and verifies the
  download against the SHA-256 digest GitHub records for the asset (shown on the
  release page), at most 512 MiB.
- Setup runs with `/DIR=<game folder>` after the launcher has restored any
  pending game files; the launcher then closes so Setup's running check passes.
- New features are marked **\*new\*** in development builds and in experimental
  builds (prerelease versions) of the version listed in
  `launcher/NewFeatures.vb`; stable releases never show the marks. If the
  release gets another number than `NextVersion`, change it before building.
  After the release, set `NextVersion` to the following version and remove the
  released entries; `NewFeaturesTests` fails until this build is before
  `NextVersion` again.

## Bounded desktop diagnostic

`--background-play` keeps the game running when it loses focus and starts it without
activating its window, so a run doesn't need the desk. `--research-patch
RVA:ORIGINAL:REPLACEMENT[,…]` with `--proxy build/native/bin/research/d3d11.dll` applies
an in-memory code change in the research-only renderer (never packaged) after its original
bytes match; the report records whether it was applied.

`python tools/run_benchmark.py` is read-only preflight. With `--run`, it stages
an owned proxy and unique benchmark XML in the game directory, and a unique
hardware XML under the actual Documents `My Games/DiRT3/hardwaresettings` folder.
It starts the selected game with the shipped benchmark route and requests exit
five seconds after a result appears, or after the bounded interval.
It sends no driving/menu input and starts no OpenXR session. A retained child
process may be terminated after the exit grace period; that is reported as a
failed clean-exit test. Never run the tool while another DiRT 3 process is open.

The deployment journal is written before changes. Recovery removes only unchanged
owned files. Existing proxies are refused. A changed file is retained as a conflict.
Use `--recover <absolute deployment.json path>` only after closing the game.
Protected executable/settings/save hashes and originals are retained locally;
unexpected changes fail the run and require review instead of blind replacement.
Formatting-only XML rewrites can be restored after checking the complete
semantic content and retaining the generated version. Real setting changes
remain conflicts. The game can force `parallelUpdateRender=true` through its
hardware restrictions even when the temporary configuration requests false;
the result parser reports this mismatch. Do not assume the requested scheduling
mode was active. The measured run and reviewed cleanup are documented in
`implementation-status.md`.

Only normal production binaries are packaged. Synthetic test proxy and fixture
executables are excluded. A successful query, build, synthetic test or desktop
benchmark is not evidence of working stereo or headset acceptance.
