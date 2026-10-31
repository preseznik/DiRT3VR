# Startup conveniences — 0.1.19

Step 6 adds one opt-in experimental control on Launcher: **Skip startup logo
movies**. It applies to desktop and VR launches and is off in old/new preferences.
Copyright, Press Start, the attract movie and later game movies remain. No
automatic game launch is authorized by this implementation task; ask first.

## Static evidence and bounded edit

The installed Steam Complete Edition's `system/boot_data.xml` points to
`system/states.bin`, not `states_steam.bin`. The decoded `flow.bin` chains
`amd_sting_video` and `sting_video` before the initial legal/Press Start path.
Each is a leaf StateVideoPreStart with id, filename, filename_us and skippable
attributes. The existing StateWayPoint type accepts timeDelay.

The 590,972-byte original has SHA-256
`353f2b8fd00a8b26d47c74c446f07f1ec1ae38cf497482b426c0547bf2159996`.
Only eight 32-bit words change: each node's type becomes StateWayPoint, its
attribute count becomes two, and its second attribute becomes timeDelay=0.0.
The id attribute is retained. The two now-unused attributes remain in place,
unreferenced; tables and string indices never shift. Patched SHA-256:
`929d24ec607126b9c99d3eb5b61e723fba630b5addc8561abf3569d39c90fe7b`.

`tools/research/inspect_startup_movies.py` independently decodes before/after,
checks exclusive ownership of each attribute range and compares the complete
semantic tree against exactly these two replacements. It also checks the boot
resource selection. Evidence and non-distributed original/patched fixtures are
under `build/research/startup-movies`. This establishes the asset transformation;
it does not establish observed in-game skipping or multiplayer compatibility.
The attended desktop result below confirms engine acceptance of this asset.

Regenerate the local test fixture (no installed writes or execution):

```powershell
python tools/research/inspect_startup_movies.py --game 'C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition' --output 'F:\Coding\Codex\Mods\DIRT3VR\build\research\startup-movies'
```

The launcher contains only the pinned transform, with no XML converter dependency
or bundled game asset. Modified, already-patched, missing or unsupported startup
files are refused. The installed executable retains its existing fingerprint gate.

## Ownership and recovery

Recovery schema 3 admits exactly one of two target sets: `startup` for desktop,
or `graphics,proxy,restrictions,startup` for VR. Schemas 1/2 remain supported and
cannot claim the new target. Originals and transformed bytes are backed up and
validated before a pending journal is written, then existing elevation workers
apply only approved fixed destinations. Startup uses exact-byte restoration;
only graphics retains the existing formatting-equivalence rule.

Desktop logo skipping holds the existing session lease through process exit and
recovery. The progress monitor leaves the process handle with that owner. Closing
the launcher window keeps DiRT3VR in the notification area until recovery
completes; it then quits (it minimized before this change, after 0.1.20).
Preparation/launch failure
recovers staged changes; forced termination or power loss leaves a pending journal
for Restore original files in 0.1.19 or later. Conflicting external edits preserve
both current files and backups. Ordinary desktop mode still uses its original
non-injected launch path. Desktop resolution choices remain persistent, separate
from this temporary startup transaction. VR includes startup in its existing
transaction and reuses the accepted native intro/initial-legal presentation.

Settings profile schema 2 includes the startup choice. Schema 1 loads it as off.
Save settings/profile operations alone never change the game asset.

## Background play and focus

DiRT2VR's BackgroundPlayLaunch requires native DX11 injection and two
fingerprinted focus-loss pause patches in background_play.cpp. Those addresses
are not DiRT3 addresses. A bounded inspection of DiRT3's 32 shipped system XML
files, current graphics XML and current mod source established no corresponding
configuration option or hook. Background play therefore remains unimplemented.
It requires independent handler discovery and replay/driving/manual-pause,
input/audio and focus-return acceptance. Existing launcher/game-owned activation,
explicit Show game and foreground-only recenter behavior remain unchanged.

## Validation and live gate

827 launcher checks pass. They cover the pinned patch, wrong/modified assets,
profile migration, three recovery schemas, partial preparation/deployment,
corrupted/forged evidence, conflicting edits and exact restoration. An owned
sleep-and-exit test helper exercises desktop process-handle ownership, exit
restoration and lease release; it does not run a game. Modern/Classic layout
checks cover 560/900/1440 logical widths, with rendered UI review at host 150% DPI.
Other actual DPI/theme combinations are not claimed.

The x86 native DLL and runtime probe are unchanged from the accepted baseline.
No native test rerun is needed for unchanged code. The independently approved
desktop and VDXR runs passed logo skipping, retained copyright/Press Start and
menus, and the VR handoff to tracked 3D menus. Desktop restoration was automatic;
VR needed review of the existing graphics XML rewrite, as recorded below.
Leave the switch off for multiplayer until its compatibility is established.

## Local package and attended results

Verified portable package:
`artifacts/packages/0.1.19-20261029-010703/DiRT3VR`.
All 22 payload files, architectures, hashes and ZIP contents passed validation.
827 launcher checks also passed against its capability data. The packaged
`--check-install` returned 0 with version 0.1.19.0, and the Steam connectivity
helper initialized successfully without starting the game. Build/test logs are
under `artifacts/diagnostics/startup-0.1.19-20261029`. NuGet audit metadata was
unavailable (NU1900); no dependency was added and builds/tests succeeded.

ZIP SHA-256: `0097a047d23f8932b09aca410a91e669a124caad2f496a30244d7a066745c3cd`.
Native DLL SHA-256: `a50f3b7f3af99cd4e16250e6d78691f5c4793383c7bab166f36e59d644ff669b`.
Probe SHA-256: `138cc17fde053f459f3f386458c38893802a02f6d4fa792da81dcc43843d2cb9`.
The installed states.bin still matched the original before the attended test.

The user approved and performed one manual desktop launch from the foreground
0.1.19 launcher, with startup skipping enabled. PID 33312 reached ready state;
the live asset matched the patched hash and the schema-3 journal owned only
`startup`. No d3d11.dll proxy was installed. The user confirmed both logo movies
were skipped while copyright, Press Start and the main menu worked.

After normal exit, the startup file matched the exact original hash, with no
pending recovery journal or proxy. Receipt
`restored-cbe69524b78c4ade9701a7bad001e087.json` records the single owned target.
Evidence is in `artifacts/diagnostics/startup-0.1.19-20261029/live.json`,
`desktop-after.json` and `restored-desktop.json`. A fresh read at
2026-10-29T00:15:34Z confirmed the game was closed and files remained restored.
The launcher remains open with the option saved. Desktop approval is consumed.

### VDXR check

The user separately approved and manually started one VDXR run at 100% with
logo skipping enabled: PID 53652, session `366a95bb4e9c4256b4bc35bfc31bf58e`.
The user confirmed the requested startup checks worked correctly. The session
record reports 3072x3264 for both scene and headset output. Native logs show
the startup screen, handoff to stereo at main frame 445, at least 1,028 submitted
stereo frames, and clean XR shutdown on game exit at 00:18:12Z.

Automatic recovery stopped on the previously observed graphics XML rewrite:
width/height 3072x3264 became 1280x720, and refresh rate 120 became 100. Complete
XML comparison after normalizing only those three reviewed values found no
other semantic differences. Original, applied and game-written copies were
preserved. After restoring the exact graphics original, the packaged file-only
recovery worker restored the restrictions/startup originals and removed the
owned proxy. No recovery code or conflict rule was relaxed.

At 2026-10-29T00:21:10Z all four targets matched their original hashes/existence,
with no game, proxy or pending journal. Receipt:
`restored-3a3e97c5f0ed4a83a5acda72d003f3fc.json`. Evidence is in
`vr-session.json`, `vr-native.log`, `vr-pending.json`, `vr-graphics-*.xml`,
`restored-vr.json` and `vr-after.json` under the diagnostic folder above.

Startup logo skipping is accepted for desktop and VDXR. This does not establish
automatic recovery for game-written resolution changes, multiplayer compatibility
or SteamVR acceptance. Background play remains deferred. Both launch approvals
are consumed; the next parity item is configurable keyboard/controller VR bindings.
