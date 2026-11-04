# DiRT3VR

[**Stable download**](https://github.com/preseznik/DiRT3VR/releases/latest) · [**All releases, including Experimental**](https://github.com/preseznik/DiRT3VR/releases) · [Changelog](https://github.com/preseznik/DiRT3VR/blob/main/CHANGELOG.md)

Open **Assets** on the release page and choose the Setup EXE (or the ZIP).
Releases marked **Pre-release** are Experimental builds with the newest
features.

A launcher for **DiRT 3 Complete Edition** (Steam) with experimental
stereoscopic VR and head tracking, and direct practice, driving controls and
seat positions on the monitor and in VR. In VR, menus, loading screens and
movies use a virtual screen.

Tested with a **Quest 3 through Virtual Desktop (VDXR) and SteamVR**, an Xbox
controller and a Logitech G920. Other headsets and OpenXR runtimes need more
testing.

This mod will remain free forever. If you'd like to support the work, you can [support me on Patreon](https://www.patreon.com/cw/bohloney).

## Requirements

- Windows 10 or 11 (64-bit) and a PC that runs DiRT 3.
- **Steam DiRT 3 Complete Edition**. Other editions and modified game files
  aren't supported.
- For VR: your headset ready in Virtual Desktop (VDXR), SteamVR or another
  OpenXR runtime with 32-bit support. Playing on the monitor needs no headset.
- A keyboard, controller, or wheel and pedals. Motion controllers aren't used.

## Install

1. Run DiRT 3 once from Steam, then close it.
2. Run `DiRT3VR-<version>-Setup.exe`. Keep the detected DiRT 3 folder (the one
   with `dirt3_game.exe`) or browse to it. Setup puts DiRT3VR in a `DiRT3VR`
   subfolder there and adds a Start menu shortcut; it doesn't change any game
   files. Or extract the ZIP to a folder of your choice.
3. Open **DiRT3VR**. Windows may ask for permission when DiRT3VR needs to write
   to the Steam folder.

DiRT3VR checks for updates at startup and shows **Update available** next to
**?**. Turn on **? → Include experimental releases** to also get Experimental
builds; **Return to stable…** goes back.

## Play on the monitor

- **Launch** starts DiRT 3 with its own menus.
- **Launcher → Launch mode → Direct practice** starts straight on a route with
  your car, with laps and up to 7 AI opponents on circuits and your choice of
  assists. After the finish DiRT 3 closes or returns to its menus.
- **Controls → Driving controls** binds your wheel, pedals, shifter or
  controller for direct practice: a binding wizard, an Xbox preset,
  **Wheel rotation…**, the **Gearbox**, and **Drives with** to pick the device
  you drive with.

## Play in VR

1. Start your headset software and connect the headset. In Virtual Desktop,
   choose **VDXR** as the OpenXR runtime.
2. In DiRT3VR, turn on **Advanced → Enable experimental VR** and choose
   **VDXR**, **SteamVR** or **Other OpenXR runtime** under
   **Settings → VR runtime**. Close other VR games.
3. Click **Launch VR**, sit facing forward, and keep DiRT 3 in front. The view
   centres itself when VR starts.
4. While driving, with DiRT 3 in front:
   - **F10** recentres the view.
   - **Tab** opens the seat menu: the arrow keys move your seat, **Enter**
     saves, **Esc** cancels.
   - **F6** switches the headset to a flat screen and back, for a view that
     looks wrong.
5. **Stop VR** keeps DiRT 3 running on the monitor; **End session** closes it.
   When DiRT 3 closes, DiRT3VR puts back the game files it changed for the
   session.

Picture options are under **Graphics → VR rendering** (render scale,
anti-aliasing, anisotropic filtering, field of view and helmet view). After a
VR session, **Graphics → Quality → Recommend for this PC** suggests settings
from that session's frame rate.

## If something goes wrong

- After a crash or a Windows restart during play, click
  **Restore original files** before launching again.
- If an add-on such as ReShade or the OBS mirror stops VR from starting,
  Launch VR names it and offers **Disable and retry**.
- If the VR renderer doesn't load, the launcher says why. Often the fix is
  the Visual C++ 2015–2022 Redistributable (x86), or an antivirus that removed
  `d3d11.dll`.
- To report a problem, click **Open logs** and attach the newest session
  folder.
- Leave **Skip startup logo movies** off for multiplayer.

Every option is explained in the launcher itself: **?** → **Instructions**, or the
**Instructions** link on each tab.
