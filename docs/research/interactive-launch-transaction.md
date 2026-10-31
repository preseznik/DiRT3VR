# Interactive experimental VR launch and recovery

Status: implemented in local launcher 0.1.2 on 2026-10-28, with fixture/offscreen validation. No game or runtime probe was launched during launcher development. Native screen fallback and conservative camera classification are integrated separately; interactive menu/camera-transition behavior still needs explicitly approved live acceptance. The user requires fresh permission before each agent-initiated game launch.

## Normal hardware filename selection

For the supported x86 executable SHA-256 `c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98`, active hardware filename global VA `0xfd8a50` initially contains pointer `0xec2b68`, the literal `hardware_settings_config.xml`.

The bounded instruction/reference search found these direct references:

| VA | Use |
| --- | --- |
| `0x6aab23` | Benchmark/demo constructor publishes its 64-byte hardware filename field into the global. |
| `0xb2e96c` | Reads current filename for a hardware-settings operation. |
| `0xb322f0`, `0xb3231c` | Hardware loader reads current filename and compares it to the default. |
| `0xb32353` | Fallback replaces current filename with the default pointer. |
| `0xb3244a`, `0xb3257c` | Subsequent hardware load/default operations read the filename. |

The constructor at `0x6aa900` recognizes `-benchmark` and `-demo`. Both activate its automated controller (`+0x80=1`); only the active branch at `0x6aab20..0x6aab23` publishes the custom filename. The `hardwaresettings` XML attribute is read by `0x6a7850`. Consequently, passing a benchmark file merely to select custom settings is not a normal interactive launch.

A bounded null-terminated ASCII switch inventory of `.rdata/.data` found `-force_dx9`, `-safemode`, `-demo`, `-benchmark`, `-logging`, `-profileopts` and other unrelated options, but no hardware filename override. The direct filename references and current parser evidence establish **no supported normal-launch override**. This is a bounded negative finding, not proof against every possible indirect/undiscovered command-line path. Do not invent a `-hardware`/`-config` argument or patch the filename pointer for this milestone.

The normal loader mounts the actual Windows Documents `My Games\DiRT3\hardwaresettings` directory as `/hwsave/`, then reads the default filename there. See `observer-followup.md` and its `static-evidence/benchmark_command_parser.asm.txt`, `benchmark_config_values.asm.txt`, `hardware_selected_file_fallback.asm.txt` and Documents/mount evidence.

## Original launcher foundation

The earlier launcher supplied this foundation; the implementation below extends it:

- `Session.vb::DesktopStartInfo` launches the exact game unelevated, with the game working directory, no game arguments, and a clean mod/runtime environment.
- `Recovery.vb::OwnedFileTransaction.Prepare` backs up originals and journals hashes before writes. Its target enum is currently only `proxy` and `graphics`.
- Recovery verifies the whole journal before restoration, preserves foreign modifications and accepts already-restored originals. It currently requires exact applied hashes, so harmless game XML serialization changes cause a conflict.
- `RecoveryWorker.RecoverAsync` elevates only recovery file operations. There is no apply worker yet.
- `LaunchProgressMonitor` correlates the owned PID/start time and rendered game window, but ends its job after startup; it is not a game-lifetime session supervisor.

Extend this implementation rather than ship Python inside the launcher or widen `DiagnosticFiles` to become a second production session manager.

## Implemented design

1. Add a schema-2 launcher recovery journal with a third fixed target, `restrictions`, resolving only to `system/hardware_settings_restrictions.xml`. Keep schema-1 recovery valid. Reuse the already tested pinned original hash and six-branch true-to-false transform from the Python diagnostic design. Verify the original/applied signature, never accept arbitrary replacement bytes for this target.
2. Temporarily replace the existing actual-Documents graphics XML. Preserve the original bytes and derive an applied copy changing only the initial supported profile: `forcedx9=false`, built-in stereo disabled, `parallelUpdateRender=false`, and the agreed tested window/resolution/MSAA/vsync values. Preserve all unrelated settings. Missing/unknown XML shape is a preflight error. Do not touch controls, career/save files, or Steam launch options.
3. Store both original and applied graphics bytes/hashes in the journal's backup directory. On recovery accept exact original, exact applied, or a current XML tree semantically identical to the applied tree. Ignore only indentation/attribute order/XML serialization differences; compare every element, attribute and meaningful text. Retain the game-written copy before restoring exact original bytes. A real settings change is a conflict: preserve current file plus backups and show recovery pending. Do not silently undo settings the user changed during the session.
4. Preflight **all three** targets and both backups before any target write. Persist the ownership journal first; recheck each hash immediately before replacement. Create a new proxy with exclusive creation so a racing foreign proxy cannot be overwritten. Use fixed target IDs, bounded hashes/backup names, no reparse paths and no arbitrary elevation-worker destination.
5. Add a file-only apply worker beside the existing recover worker. The initiating unelevated launcher records the actual graphics path, install ID, session ID, expected executable hash and approved package payload/hash; the worker validates that receipt and the fixed target mapping. It only deploys/restores files. Preferences, runtime resolution/probing and the game process stay with the initiating user; never launch the game elevated.
6. Add an owned interactive session in `Session.vb`: take a per-user game-session lock (different installs share Documents settings), require all DiRT 3 processes closed, validate executable/payload/runtime and pending recovery, prepare/deploy, then start normal `dirt3_game.exe` with **no benchmark or demo arguments**. Launch failure restores the journal immediately. Record PID/start time for progress and lifecycle checks.
7. Keep the launcher process alive through the owned game lifetime and restore after its exit. Closing the launcher window during VR should minimize/hide it with a clear running status, not abandon restoration or kill the game. Reuse the existing modeless startup popup; Hide/Escape still dismisses only that popup. A killed launcher/Windows interruption leaves the durable journal for next-start recovery.
8. Give each session a fresh log directory and owned stop-request path. The normal interactive environment selects only the chosen explicit x86 runtime. Native code must support a distinct interactive mode with immediate screen fallback and guarded cockpit rendering; do not expose the diagnostic prototype's timed benchmark activation as the finished interactive contract. Explicit launcher stop/close requests should request render-thread XR cleanup before WM_CLOSE. Ordinary in-game Quit/Alt+F4 also needs native cleanup; a file handshake alone cannot guarantee it after automatic process exit.

The initial profile uses tested 1280×720 windowed, no-MSAA, no-vsync settings. Graphics describes this as a temporary session profile. Headset render scale is fixed at 50%; F9 recenters only with the game in the foreground. There are no unrelated new graphics controls.

## Honest experimental launch gate

The persisted `ExperimentalVrEnabled=false` preference is an explicit Advanced opt-in. `RuntimeCapabilities.RequireExperimental` additionally requires matching package `experimentalInteractive=true` and `vdxr` in its runtime list; production `stereoVerified` remains false. The compiled launch policy permits only VDXR. SteamVR runtime probes remain available, but interactive SteamVR is blocked with an orientation-validation explanation until an isolated retest is accepted. The launcher resolves its selected x86 runtime; the native session establishes actual runtime/headset availability.

The existing Launch VR button requires opt-in, matching package support, VDXR selection and no pending recovery; prerequisites are rechecked on click. During the owned session it becomes Stop VR / quit. The launcher minimizes instead of closing while a session or recovery is active. It states that menus use a virtual screen, recognized cockpits use tracking, and pause/replay can retain cockpit tracking. Existing tabs, footer arrangement and yellow accents remain.

Normal Launch retains the original desktop path and clears all VR environment flags. Both launch paths share the per-user session lock, because different installations share Documents settings. Startup progress distinguishes the game window from headset output. A passive tail of the owned PID's log updates session status on actual screen/cockpit submissions or explicit native stopped/failed/refused records; it does not launch probes, close the game or repeatedly show dialogs.

The exact interactive environment is `DIRT3VR_DIAGNOSTICS=1`, `DIRT3VR_SCENE_DIAGNOSTICS=1`, `DIRT3VR_XR_PROTOTYPE=1`, `DIRT3VR_XR_INTERACTIVE=1`, plus the selected `XR_RUNTIME_JSON`, session `DIRT3VR_LOG_DIR` and absolute owned `DIRT3VR_XR_STOP_FILE`. No game arguments are passed. The payload is `payload/d3d11.dll` beside the launcher. Elevated apply/recover workers validate the fixed target receipt and do file operations only; the game is started by the original unelevated launcher.

XML comparison retains comments and processing instructions, including outside the root; only whitespace-only text and attribute ordering are ignored. Schema-1 journals remain recoverable. Schema-2 validates original/applied backup hashes and the fixed transformations. It retains game-written formatting before restoring the exact original bytes. A genuine settings/comment/PI edit, unexpected restriction hash, missing backup or foreign proxy stops recovery without deleting the other owned targets.

## Required verification

Fixture tests cover three-target staging before deployment, interrupted staging/recovery, schema-1 compatibility, malformed/null fields and unknown targets, foreign proxies, restrictions hash drift, graphics formatting-only restoration versus real edits/comments/PIs, damaged backups, cross-install session locking and stale PID/progress events. They also cover explicit opt-in/package/runtime gates, unelevated normal-game arguments, isolated child environment and passive native status. Actual process creation, UAC behavior and automatic recovery after an interactive game session remain live acceptance items. The pinned 5,957-byte restrictions XML fixture is test-only and is not part of a user package.

Then run one explicitly authorized interactive session for each selected runtime: menu screen, event entry, accepted cockpit, rear view, pause/replay/frontend transition, in-game Quit and Alt+F4, followed by exact original hashes and no pending journal. Game/headset behavior and recovery are separate checks; an automated benchmark does not replace this acceptance.
