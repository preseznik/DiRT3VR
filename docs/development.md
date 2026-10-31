# Development

Requirements: Windows x64, Visual Studio C++ x86 tools with CMake/Ninja,
Python 3, Git and .NET 10 SDK. Native outputs target x86; the launcher targets x64.

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

## Bounded desktop diagnostic

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
