# Passive observer follow-up — 2026-10-28

This follow-up reads the existing diagnostic output and the fingerprint-verified
executable. It did not launch or attach to the game. The final run verifies a
completed Aspen desktop benchmark and the main render boundary. Safe same-frame
stereo replay remains unproven, and the game overrides the required scheduling
setting back to parallel update/render.

## What the existing run establishes

Source: `artifacts/diagnostics/20261028-090015-4c35cbb4/native-80420.log`, SHA-256
`d92105c0a94cabd0c9dd420928878d2d5b8e6f74d6ae40c60caa5a37cdd9795c`.

The log contains 391 sampled main outer/inner pairs, reaching main count 46,800.
Every sample has one first inner call, no second call, no identity mismatch, no
changed inline camera bytes, and a render serial increase of one. Of those
samples, 180 have a changed list-header hash; the first is main count 8,520.
These are samples, not a claim that every intervening frame had identical state.
Camera equality is before/after each invocation, not equality across frames.

The run report records a clean exit after a requested close at 90 seconds and
restored protected files. The swapchain used 2560×1440 although the custom
diagnostic hardware XML requested 1280×720. No matching benchmark result was
reported. This establishes pass-through rendering and observed state mutation;
it does not establish that the requested Aspen race loaded or completed.

## Final run: benchmark confirmed, scheduling requirement failed

The subsequent run is
`artifacts/diagnostics/20261028-092210-d2488aa9/native-67528.log`, SHA-256
`c9001718d02997a5fb0b98d00913fb70c490b8488dce694d4405a1c557aafccc`.
It used proxy SHA-256
`4ea87f0a2160296a1c39507b76840d2cf20a80080afefde89c5b911f76568814`.
The matching unique result file is
`DiRT3VR_d2488aa9e2e944d18fcfee6d28c37952_09-24-16_on_28-10-2026.xml` in that
artifact directory. It reports `aspen`, `route_0`, car `frx`, DirectX 11 and
47,978 benchmark samples. The process exited normally with code 0 after
126.09 seconds. Both the swapchain observation and result configuration show
the requested 1280×720 windowed resolution and native stereo disabled.

There are 478 sampled main outer/inner pairs, reaching main count 57,240.
All sampled calls have one first inner render, no second/unexpected inner call,
no identity mismatch, no changed inline camera bytes, and render serial +1.
Present counts are identical at entry and exit of each sampled outer and inner
call. This locates these calls between Presents; it does not prove that another
thread cannot update simulation or that a second render is safe.

Of the sampled headers, 296 changed. The four bounded offset-detail records
(main 9,240 / 9,360 / 9,480 / 9,600) each report exactly four changed bytes:
`0x84..0x87`, without truncation. This matches the four-byte pointer field
written by `0xc16c20`, narrowing the candidate substantially. It is not a
write-site trace, and it does not show every changed offset in later samples.

The input custom hardware XML has `parallelUpdateRender="false"`. The game
rewrote it to `true`; the benchmark result also records `true`. The reviewed
comparison found this was the sole semantic XML change. Consequently, this run
does **not** pass the single-threaded update/render requirement. The automatic
recovery correctly retained the changed, owned XML as a conflict. The separate
`reviewed-recovery.json` records subsequent preservation of that evidence,
removal of owned temporary deployment, and unchanged original protected files;
the initial `run.json` remains preserved with its recovery failure.

This is completed desktop benchmark acceptance with passive scene observation.
It provides no interactive cockpit, stereo image, tracking, state restoration,
or VDXR/SteamVR acceptance. Milestone 1's safe two-view replay gate remains open.

## Concrete list-header mutation candidate

The helper at VA `0xc16c20` is conditionally called from the main prepass at
`0x580fde` and main colour pass at `0x581498`. Its first stack argument is the
prepared list header. It:

- writes header `+0x84` at `0xc16ca6` / `0xc16cec`;
- writes header `+0xd8` at `0xc16cb5` / `0xc16cfb`;
- can clear both fields at `0xc16d10` / `0xc16d16`;
- calls `0x8c67d0`, which adds to header `+4` at `0x8c67ed` and updates header
  `+0` at `0x8c67fe`;
- clears its own pending flags `+0x40` and `+0x41`, and invokes list-building
  and sorting helpers. Those objects are outside the sampled header.

The first run's hashes cannot attribute the changes to this helper. The final
run's four offset samples match its `+0x84` field, while no changes to `+0/+4`
or `+0xd8` were reported in those four samples. Other writes may exist.
The comparison uses the already copied 0x108-byte header, without additional
hooks, pointer traversal, or game-state writes. Header restoration alone would
still leave owner flags, collection nodes and GPU effects unresolved.

Evidence: [writer](static-evidence/late_list_header_writer.asm.txt),
[aggregate update](static-evidence/list_aggregate_update.asm.txt),
[prepass call](static-evidence/main_prepass_header_writer_call.asm.txt),
[colour call](static-evidence/main_colour_header_writer_call.asm.txt).

## Benchmark arguments and hardware-file lookup

The shipped `example_benchmark.xml` documents `-benchmark <filename.xml>`.
The parser at `0x6aa900` confirms that spelling and order: the comparison at
`0x6aa9bf` checks `-benchmark`, and `0x6aaa96..0x6aaaae` passes the next token
to `0x6a8800`. With no next token it uses `example_benchmark.xml`. The XML reader
passes the supplied filename directly to the engine file service; it does not
prepend the hardware-settings directory. Thus the runner's benchmark switch
and argument shape match the executable.

The `hardwaresettings` attribute has different semantics. The XML parser copies
it into a 64-byte filename field (`0x6a7977..0x6a7985`); the constructor assigns
that field to global `0xfd8a50` at `0x6aab23`. The hardware loader first requests
`/hwsave/<filename>` (`0xb322f0..0xb32308`, formatting in `0xb2e400`). On failure,
it restores the default `hardware_settings_config.xml` name and retries
`/hwsave/` at `0xb3234d..0xb32365`; it can then try `system/` at
`0xb3244a..0xb32461`.

The normal `/hwsave/` mapping is the user's actual Documents folder followed by
`My Games\DiRT3\hardwaresettings\`:

1. `0x624cd0` calls the imported `SHGetFolderPathA` with folder ID 5, then
   appends `My Games` and `DiRT3`; `0x624d90` returns this base path.
2. `0x59bfc3..0x59bfd7` copies that base, which is passed by `0x59c259` to
   `0x579fc0`, then by `0x57a037` to `0xb2e1a0`.
3. `0xb2e1d4..0xb2e1e3` appends `hardwaresettings/` into the object's path
   field. Initialization temporarily mounts the parent path to create/check the
   subdirectory. Before loading files, `0xb32292` reads the full path pointer
   from object `+0x0c`; `0xb322b2..0xb322d4` mounts that directory as `/hwsave/`.

For this user's ordinary Documents resolution, the directory is
`C:\Users\iztok\Documents\My Games\DiRT3\hardwaresettings\`. A runner should
resolve Documents rather than hard-code this path, create its unique owned XML
there, and retain the filename alone in the benchmark attribute. Its reversible
transaction must cover this additional directory and possible game edits to
the owned XML. A file placed beside the executable is outside that normal lookup.

The 2560×1440 observation is consistent with fallback to saved settings. It
does not prove that `-benchmark` was ignored. The saved configuration also has
`parallelUpdateRender="true"`, so the original run cannot claim that the
requested false setting was active. Verify the selected configuration and the
observed swapchain before interpreting a run as the planned preset. The final
run verified the path and resolution, but also exposed the scheduling override.

Evidence: [command parser](static-evidence/benchmark_command_parser.asm.txt),
[XML reader](static-evidence/benchmark_xml_reader.asm.txt),
[attributes](static-evidence/benchmark_config_values.asm.txt),
[Documents root](static-evidence/user_documents_root.asm.txt),
[virtual mount](static-evidence/hardware_virtual_mount.asm.txt),
[active-directory mount](static-evidence/hardware_active_directory_mount.asm.txt),
[file reader](static-evidence/hardware_file_reader.asm.txt),
[saved-file fallback](static-evidence/hardware_selected_file_fallback.asm.txt),
[system fallback](static-evidence/hardware_system_file_fallback.asm.txt).

## When the benchmark writes results

The constructor calls `0x6a7350` to create the benchmarks directory. This does
not write a result file. The observed completion path at `0x6a8720` increments
the completed-track count at `+0xefc`, compares it with requested count `+0xef8`
at `0x6a87ce`, and calls the result writer `0x6a73d0` only when they match
(`0x6a87d8`). The writer constructs
`<Documents>\My Games\DiRT3\benchmarks\<outputfilename>_<timestamp>.xml` and
opens/writes the file at `0x6a77de..0x6a77fd`.

Therefore, closing a run after 90 seconds can leave no result even if a benchmark
started. A completed result with the unique run prefix and expected track is
stronger acceptance evidence than process longevity or Present counts. The
final run provides that result; the first run remains an unconfirmed desktop
observation. This remains separate from cockpit, two-eye, tracking, effect,
and VDXR/SteamVR acceptance.

Evidence: [directory creation](static-evidence/benchmark_result_directory.asm.txt),
[completion condition](static-evidence/benchmark_track_completion.asm.txt),
[result writer](static-evidence/benchmark_result_writer.asm.txt).

## Scheduling override evidence and remaining work

The shipped `system/hardware_settings_restrictions.xml` contains CPU
`restrict_add` entries that set `parallelUpdateRender="true"` for core counts
2, 3, 4, 6 and >=8, plus a hyperthreading condition. Its >=8-core entry also
selects `system/workerMap8Core.xml`, matching the final result. The fallback
entry is false. The [preserved CPU excerpt](static-evidence/hardware-cpu-restrictions.json)
records the source file hash. These restrictions are a concrete, consistent
explanation for the observed rewrite; their application was not traced live.

The main-loop function `0x584b90` actually reads the boolean from
`cpu/threadStrategy/parallelUpdateRender`: `0x584bd3` supplies a default of true,
and `0x584be5` calls the boolean reader `0xcd2cf0`. The helper compares the
attribute text with `true` and writes the result to the supplied local byte.
The branch at `0x584d84` selects distinct call ordering; false branches to
`0x584e01`. Thus the observed consumer is not a hard-coded forced-true branch.
Evidence: [consumer](static-evidence/parallel_update_render_consumer.asm.txt),
[boolean reader](static-evidence/xml_boolean_reader.asm.txt).

The next implementation step needs a reversible, verified way to keep the
effective runtime value false after restrictions are applied, followed by the
same bounded desktop benchmark and effective-setting verification. Changing
the input XML alone has now failed that check. No stock restrictions, executable
bytes, or runtime branches were changed by this investigation. Even resolving
this scheduling issue would not resolve shared-list/owner/GPU restoration.

All focused byte ranges and strings are reproduced by
`tools/research/inspect_render_boundary.py`; the manifest retains the executable
SHA-256 and individual range hashes. No new whole-program decompilation was used.
