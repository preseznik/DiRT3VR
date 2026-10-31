# Desktop display controls — 0.1.17

Step 4 of the launcher parity plan is implemented. The user confirmed Borderless
at the display's native 3840×2160 and Windowed at 1920×1080, including Alt+Tab and
normal quit, in 0.1.17. VSync On was applied and retained in the game XML.
Fullscreen starts correctly at 1920×1080 from the foreground launcher, but the
game switches to native 4K after returning from Alt+Tab. Retaining the selected
fullscreen resolution remains unresolved.
No game launch was performed during
implementation or automated checks.

- Defaults: resolution 0×0, mode `game`, VSync `game`. Older preferences load
  these defaults without rewriting either preferences or game graphics.
- Resolution choices come from the main display's Windows 32-bit display modes.
  A saved size remains selectable after a display change. Bounds are 640×480
  through 8192×8192. Refresh rate, aspect, MSAA and all unrelated fields remain
  under the game's control.
- Windowed/fullscreen map to DiRT3's existing `fullscreen` boolean. VSync maps
  to `vsync=0/1`. Borderless writes windowed mode and the main display dimensions.
- Save stores preferences only. Desktop Launch applies explicit choices to the
  user's graphics XML after executable/closed-game/pending-recovery checks.
  It creates an exact original backup in the install's `desktop-backups` folder
  before an atomic write. These are persistent choices, including if the game
  fails to start; they are not a temporary session and are not auto-restored.
  Reapplying the same settings does not rewrite the file or create extra backups.
- A missing graphics file requires one default launch through the native game.
  Malformed XML, missing/duplicate resolution entries, read-only files, changed
  backups and pending recovery are refused without overwriting the source.
- Borderless applies only to the launched PID/start time and its rendered
  `NeonClass_41` window. It waits up to five seconds for frame/placement confirmation,
  restores original placement/styles on failure or interrupted setup, and
  does not force focus or repeatedly undo later user window changes.
- VR uses the existing ExperimentalFiles profile and recovery. The accepted
  0.1.16 native DLL is unchanged; desktop settings do not reach that path.

Automated checks cover migration, per-install settings, XML field isolation,
exact backups, invalid input, VR separation, foreign-window refusal, actual
owned WinForms borderless placement, rollback including topmost state, interrupted
placement, retained user changes, unsaved controls and Modern/Classic layouts at
560/900/1440 logical widths. They do not establish game display-mode behavior or
actual VSync presentation. The UI was rendered and inspected at the current 150%
Windows DPI. Other DPI settings remain open; the fullscreen/Alt+Tab limitation
is recorded below.

## First live desktop run

The user launched and closed the prepared 0.1.17 launcher session manually.
The launcher recorded game PID 4140, requested at 22:23:30 UTC and ready at
22:23:43 UTC, with `NeonClass_41` and no borderless warning. The successful
placement path verifies the monitor bounds and removal of the window frame.
The resulting graphics XML is 3840×2160, fullscreen false, VSync 0 and refresh
120. The user confirmed the display was filled correctly, Alt+Tab worked and
they closed the game normally. The exact original graphics backup is intact;
no proxy or recovery journal remains.

VSync stayed at **Use game setting**, so the intended VSync On check was not
performed. The automation setup stopped before invoking Launch; once the user
started the game, its process guard refused another launch. This approval is
consumed by that user-confirmed run. Evidence is under
`artifacts/diagnostics/desktop-display-0.1.17-20261028/run-1`.

## Windowed resolution and VSync On

The second approved run used the packaged launcher's actual controls to select
Windowed, 1920×1080 and VSync On, followed by one desktop Launch invocation at
22:31:32 UTC. Accessibility setup initially failed before invocation; direct
Win32 ComboBox selection and change notification then verified each choice.
No extra game launch occurred during those setup failures.

Game PID 10856 reached `NeonClass_41`. A per-monitor-DPI-aware read measured a
1920×1080 client area with the normal window frame present. The game XML retained
1920×1080, fullscreen false and VSync 1. The user confirmed correct presentation,
working Alt+Tab and normal quit. The exact preceding desktop graphics backup is
intact, with no proxy or pending recovery. Evidence is under
`artifacts/diagnostics/desktop-display-0.1.17-20261028/run-2-windowed`.

This verifies applying the VSync setting; Present timing and synchronization
behavior were not separately measured. Fullscreen remains unaccepted. No headset
is needed for the remaining display-mode check.

## First fullscreen check: failed, with a test-side confounder

The approved Fullscreen 1920×1080/VSync On launch at 22:36:58 UTC reached the
render window (game PID 45756). XML retained the requested settings. The measured
client was only 822×656 without a frame, on the unchanged 3840×2160 display; the
user confirmed it stayed small or looked wrong after foregrounding it.

The measurement helper called `ShowWindow(SW_RESTORE)` unconditionally before
reading the window, followed by `SetForegroundWindow`. This can change fullscreen
placement, and no untouched measurement exists for this run. Later measurement
showed the game was not foreground. These are test-side confounders, not proof
of a launcher defect or an explanation confirmed by a control run. Fullscreen
must not be marked passed. Evidence is under
`artifacts/diagnostics/desktop-display-0.1.17-20261028/run-3-fullscreen`.

Next: a separately approved control launch, recording window bounds, foreground
state and display mode without calling any game-window show/restore/activation
API. The first fullscreen approval is consumed. Product code is unchanged while
the test artifact is isolated.

## Untouched fullscreen control and native evidence

The separately approved control launched PID 39372 at 22:45:22 UTC. The previous
owned process, PID 45756, was still alive despite the user's closed-game report;
a normal close request completed before the new launch. The control observer
made no game-window show/restore/activation/resize calls. It still measured an
822×656 client, no frame, no foreground focus, and an unchanged 3840×2160 display.
Thus the earlier observer's Restore call does not explain the reproduced failure.

REA opened the same fingerprinted executable without running it; focused GNU
objdump evidence and passive renderer-memory reads are under
`artifacts/diagnostics/desktop-display-0.1.17-20261028/fullscreen-research`.
Static VA 0x7c8e6e stores the renderer in GWLP_USERDATA. The known renderer layout
then allowed a bounded 364-byte ReadProcessMemory read, without patching or
injecting anything. Live image base was 0xa20000. The renderer held fullscreen=1,
engine size 1920×1080 and a requested swapchain descriptor with Windowed=0,
1920×1080 at 120 Hz. These are game-owned requested fields, not an actual
IDXGISwapChain::GetDesc/GetFullscreenState query.

At static VA 0x7c90d8 the game calls swapchain vtable slot 10 (SetFullscreenState)
and jumps onward without checking the result. No HRESULT was captured. Microsoft
documents that [SetFullscreenState can fail when the window lacks keyboard focus
or is occluded](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate),
and [CreateSwapChain can return a windowed chain with DXGI_STATUS_OCCLUDED for an
unavailable fullscreen request](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgifactory-createswapchain).
This supports a startup focus/occlusion hypothesis, not a confirmed failure code.

The user located the background game and confirmed Alt+Enter entered fullscreen,
but at 4K. The process then exited before a second memory read. Its game-written
XML changed to 3840×2160, fullscreen=true, VSync=1, corroborating the reported
resolution change. Fullscreen at the requested 1920×1080 remains unaccepted.
Evidence: `run-4-fullscreen-control/after.json` and `after-alt-enter.json`.

The original launcher was also closed. A fresh, ordinary visible 0.1.17 launcher
(PID 32812) was prepared with the saved Fullscreen 1920×1080/VSync On choices.
Windows refused programmatic foreground activation (SetForegroundWindow=false),
so another background automated launch would repeat an unresolved test condition.
No game was started. The proposed next control uses an actual user click on
Launch from the foreground launcher, followed only by the read-only observer in
`run-5-foreground`. It needs fresh user approval. Product and package binaries
remain unchanged.

## Foreground fullscreen launch and Alt+Tab resolution change

The user approved one manual launch and clicked Launch in the foreground
0.1.17 launcher at 23:07:17 UTC. No automated launch invocation occurred.
The read-only observer measured game PID 27308 in the foreground, with a frameless
1920×1080 client at (0,0), and the actual display at 1920×1080/120 Hz. XML also
held 1920×1080, fullscreen=true and VSync=1. The user confirmed it looked correct.
This establishes initial fullscreen resolution selection from a foreground
launch. It does not establish the exact failure code in the earlier background
launches, which remained at the small fallback window size.

The user then confirmed Alt+Tab worked, but returning to fullscreen changed to
4K. The game had exited before the next observation; its saved graphics XML
contained 3840×2160, fullscreen=true and VSync=1. This corroborates the report
but is not a live post-Alt+Tab swapchain measurement. The launcher's saved choice
remains Fullscreen 1920×1080/VSync On, so a subsequent launch reapplies that choice.
No VR proxy was installed for this desktop session. The original graphics backup
is verified and no recovery journal remains.

The selected fullscreen resolution is therefore not retained across Alt+Tab.
Do not mark that lifecycle check passed or treat Alt+Enter, which also selected
4K in the prior run, as a fix. Native 4K has four times the pixel count of 1080p;
the actual performance effect was not measured. No runtime workaround or binary
change was made. Evidence: `run-5-foreground/before-alt-tab.json`,
`after-alt-tab.json` and `after.json` under the desktop-display diagnostic folder.
The manual-launch approval is consumed. Any further game launch needs new approval.
