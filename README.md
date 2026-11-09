<div align="center">

<img src="docs/images/icon-battery.png" width="84" alt="Battery widget icon">&nbsp;&nbsp;&nbsp;<img src="docs/images/icon-media.png" width="84" alt="Volume & Media widget icon">

# Taskbar Widgets

**Battery levels of all your devices, plus what's playing and a per-app volume mixer, right on the Windows 11 taskbar.**<br>
Two tiny native widgets that sit side by side, never cover your icons, and use about 15 MB of RAM each.

[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?style=for-the-badge&logo=windows11&logoColor=white)](#requirements)
[![C#](https://img.shields.io/badge/C%23-.NET_Framework_4.8-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](#-build-from-source)
[![RAM](https://img.shields.io/badge/RAM-~15_MB-22C58C?style=for-the-badge)](#-performance)
[![No install](https://img.shields.io/badge/runtime-nothing_to_install-A86C3E?style=for-the-badge)](#-install)
[![License: MIT](https://img.shields.io/badge/license-MIT-lightgrey?style=for-the-badge)](LICENSE)
[![Install with AI](https://img.shields.io/badge/install-ask_your_AI-8B5CF6?style=for-the-badge)](#-ask-an-ai-to-install-it)

[![Release](https://img.shields.io/github/v/release/W1nOfGood/taskbar-widgets?style=flat-square&label=latest&color=A86C3E)](https://github.com/W1nOfGood/taskbar-widgets/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/W1nOfGood/taskbar-widgets/total?style=flat-square&color=22C58C)](https://github.com/W1nOfGood/taskbar-widgets/releases)
[![Stars](https://img.shields.io/github/stars/W1nOfGood/taskbar-widgets?style=flat-square&color=F5A623)](https://github.com/W1nOfGood/taskbar-widgets/stargazers)

<br>

<img src="docs/images/hero.png" alt="Both widgets on the taskbar with their panels open" width="100%">

<sub>Screenshots use demo data. The device names and track are made up.</sub>

</div>

---

## ✨ Highlights

<table>
<tr>
<td width="50%" valign="top">

### 🔋 Battery widget
- **Bluetooth** headphones, earbuds, speakers, mice, keyboards and controllers (classic and LE)
- **Sony** headphones read straight from the headset (accurate, unlike Windows' cached value)
- **Razer** and **Logitech** wireless receivers, **AirPods / Beats**, standard **Bluetooth LE** battery service
- **Laptop** battery
- Instant connect/disconnect, **low-battery** and **fully-charged** alerts
- **Time left** for every device ("~5 h left", "full in ~40 min"), learned from how *you* use it
- Quiet by design: one low-battery alert per discharge, no popups when a mouse just falls asleep
- Blink or pulse effect when a device connects

</td>
<td width="50%" valign="top">

### 🎵 Volume & Media widget
- **Now playing** from Spotify, Apple Music, browsers… with **album art** and ⏮ ⏯ ⏭
- Album art, seekable timeline, switch between players
- **Scroll changes only the music app**, so your game keeps its volume (Shift = everything, Ctrl = voice chat)
- **Music ducks while friends talk** in Discord, Teams, Zoom… then fades back
- **Study mode**: music lowers while a lecture or video plays in your browser, and the course keeps its volume
- **Song leveling**: every song plays at about the same loudness, even when the app's own Sound Check misses
- **Per-app volume mixer** with live meters, **remembers each app's volume** (games that reset themselves get yours back)
- **Switch output device** in one click

</td>
</tr>
</table>

### 🔄 Always up to date
- Checks GitHub for a new release once a day and can **install it automatically**: download, SHA-256 check, safe swap with rollback, restart. Both switches are in **General → Updates**.

### 🧠 Smart about your taskbar
- **Never covers taskbar icons.** It reads where the Start button, app icons and tray actually are. When space gets tight it shrinks (shorter title, compact battery view) or moves to the next free gap.
- **Auto-hide taskbar**: slides up and down with it, smoothly.
- Works with **centered or left-aligned** icons, **smaller taskbar buttons**, **labels on**, Widgets/Copilot/search box, **multiple displays**, and top or side taskbars.
- Hides for **full-screen games and videos**, but not for maximized windows.
- Survives an **Explorer restart** and DPI or resolution changes.

---

## 📸 Screenshots

| Battery: devices | Battery: design |
|:---:|:---:|
| <img src="docs/images/battery-devices.png" width="380" alt="Battery devices panel"> | <img src="docs/images/battery-design.png" width="380" alt="Battery design settings"> |
| **Media: player** | **Media: mixer** |
| <img src="docs/images/media-player.png" width="380" alt="Media player panel"> | <img src="docs/images/media-mixer.png" width="380" alt="Per-app volume mixer"> |

<div align="center">
<img src="docs/images/widget-media.png" alt="Volume & Media widget on the taskbar">&nbsp;&nbsp;<img src="docs/images/widget-battery.png" alt="Battery widget on the taskbar">
</div>

---

## 🚀 Install

<table>
<tr>
<td width="55%" valign="top">

1. Download **[`TaskbarWidgetsSetup.exe`](https://github.com/W1nOfGood/taskbar-widgets/releases/latest/download/TaskbarWidgetsSetup.exe)** (about 400 KB).
2. Run it, pick what you want, and click **Install**. No admin rights needed.
3. Done. Both widgets appear on your taskbar, start with Windows and update themselves.

**Uninstall:** *Settings → Apps → Installed apps → Taskbar Widgets → Uninstall*, or *Start menu → Uninstall Taskbar Widgets*. It closes the widgets, puts back any music volume they had lowered, and removes everything. Deleting your settings is optional.

**Update or change options:** run the setup again.

</td>
<td width="45%" valign="top">
<img src="docs/images/setup.png" alt="Taskbar Widgets setup window" width="100%">
</td>
</tr>
</table>

> [!NOTE]
> The apps aren't code-signed, so Windows SmartScreen may warn on first launch. Click **More info → Run anyway**, or use the one-line install below, which doesn't trigger it.

### ⚡ Or in one line

Paste this into **PowerShell** (Start menu → type `PowerShell` → Enter):

```powershell
irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1 | iex
```

It downloads the latest release, checks that the file matches its published SHA-256, and installs it silently. It needs no admin rights. Run it again any time to update. The script is short and easy to read: [`install.ps1`](install.ps1).

### 🤖 Ask an AI to install it

Not sure how? Let your assistant do it. Copy this into **Claude, ChatGPT, Gemini, Copilot** or any other AI:

```text
Please install Taskbar Widgets on my Windows PC.
Instructions for AI assistants: https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/AGENTS.md
```

An agent that can run commands on your PC installs it for you and checks that it's running. Examples are Claude Code, Codex, Gemini CLI and Copilot. A chat assistant walks you through it step by step instead. The instructions are in [`AGENTS.md`](AGENTS.md), with a summary in [`llms.txt`](llms.txt). They also cover updating, uninstalling and fixing common problems.

<details>
<summary><b>Portable version, silent install, uninstall from the command line</b></summary>

- **Portable:** download [`TaskbarWidgets.zip`](https://github.com/W1nOfGood/taskbar-widgets/releases/latest/download/TaskbarWidgets.zip), extract it anywhere and run the `.exe` files (keep them in the same folder).
- **Silent install** (for scripts): `TaskbarWidgetsSetup.exe --quiet`
- **Silent uninstall:** `"%LOCALAPPDATA%\Programs\Taskbar Widgets\Uninstall.exe" --uninstall --quiet` (add `--delete-settings` to remove settings too)
- **One-line script options:** `& ([scriptblock]::Create((irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1))) -Uninstall` (also `-DeleteSettings`, or `-Window` to open the setup window instead)
</details>

### Requirements
- Windows 11 (Windows 10 should work but isn't tested yet)
- .NET Framework 4.8, which is already part of Windows 10 and 11

---

## 🖱️ Using it

| Action | Battery widget | Volume & Media widget |
|---|---|---|
| **Click** | Open the panel | Media buttons, or open the panel |
| **Right-click** | Menu: position, settings, exit | Same, plus mute and sound settings |
| **Drag** | Move it along the taskbar | Move it along the taskbar |
| **Scroll** | – | Volume of the **music app only** (games untouched) |
| **Shift + scroll** | – | Master volume (everything) |
| **Ctrl + scroll** | – | Voice chat volume (Discord, Teams, Zoom…) |
| **Middle-click** / click the ♫ % | – | Mute / unmute the music app |

> [!TIP]
> **Studying with music?** Play a lecture or course video in your browser and the music app (Spotify, Apple Music…) lowers automatically while the teacher talks. The lecture's volume is never touched. Real music apps always count as "the music", even when a video is also playing.

> [!TIP]
> **Some songs too loud, others too quiet?** Set **Mixer → Music volume → Level song volume** (Gentle / Normal / Strong, on Normal by default). The widget measures how loud each song's loud parts get and adjusts only the music app. It learns your typical level over the songs you play. Typical songs play a few dB under your volume so quiet ones can be lifted; raise the master volume a little if everything feels slightly quieter.

> [!TIP]
> **Gaming with music and voice chat?** The widget lowers only the music when you scroll, and while someone talks in Discord (or Teams, Zoom, Slack, TeamSpeak, Mumble…) it ducks the music automatically by 60%, then fades it back. Your game volume never changes. Set the amount in **Mixer → Music volume**, or switch the scroll back to master volume in **General**.

**Position options:** *Next to Start button* · *Left side of taskbar* · *Next to clock / tray* · *Where I drag it*

---

## 🔌 Supported devices

| Brand / type | How the level is read | Status |
|---|---|---|
| **Any Bluetooth device** (classic & LE) | Windows' own Bluetooth battery value (as in *Settings → Bluetooth & devices*), with live connect/disconnect | ✅ tested |
| **Sony** headphones & earbuds | Asked **directly from the headset** over Sony's control channel, so it's accurate even when Windows shows a stale value | ✅ tested (ULT WEAR) |
| **Razer** wireless (2.4 GHz) | Razer's HID protocol, the same one Synapse uses, including charging state | ✅ tested (Viper V3 Pro) |
| **Bluetooth LE** devices with the standard Battery Service | Read directly from the device (GATT 0x180F) when Windows has no value | 🧪 untested |
| **Apple AirPods / Beats** | Decoded from the battery info they broadcast (left, right, case) | 🧪 untested |
| **Logitech** Unifying / Bolt / Lightspeed | Logitech's HID++ 2.0 protocol (unified battery, battery status or voltage) | 🧪 untested |
| Laptop battery | Windows power status | ✅ |

🧪 = built from the public protocol documentation but not yet tested on real hardware. Reports welcome!

### ⏱️ Battery time left

The widget follows each device's discharge and charge runs while it's connected and learns how fast it usually drains and charges. You see:

- **Connected:** "~5 h 20 min left", or "full in ~40 min" while charging (in the panel, the hover tooltip and the low-battery alert)
- **Not connected:** "full charge lasts ~22 h", from past use
- **Laptops:** Windows' own estimate

Estimates show up after about 20 minutes of use, or right away once a device has history, and they get more accurate over time.

> [!TIP]
> **Level looks wrong or stuck?** Many headsets report their battery to Windows only when they connect, so Windows' value can be hours old. Sony headsets are read directly. For other brands, turn the device off and on so it reports again.

Not yet supported: SteelSeries, Corsair and HyperX dongles, which each use a private protocol. PRs welcome!

---

## ⚡ Performance

| | Battery widget | Volume & Media widget |
|---|---|---|
| Memory (Task Manager) | ~8–17 MB | ~3–15 MB |
| CPU at idle | ~0 % (event-driven) | ~0 % (event-driven) |
| Disk | ~125 KB | ~130 KB |

How it stays this light:
- **No runtime to install, no WPF, no Electron.** It's plain WinForms with fully custom drawing.
- **Events instead of polling**: Bluetooth watcher, media session events, and Windows events for taskbar movement.
- **Raw COM** for UI Automation and Core Audio, so no extra managed libraries get loaded.
- Memory is given back after 2 minutes of idle, and never right after you use it, so clicks stay instant.

---

## 🔧 Build from source

No SDK needed: the build uses the C# compiler that ships with Windows.

```powershell
git clone https://github.com/W1nOfGood/taskbar-widgets.git
cd taskbar-widgets
.\build.ps1 -Run            # builds both widgets + the installer, and starts the widgets
.\build.ps1 -Only battery   # just one app
# release files end up in dist\: TaskbarWidgetsSetup.exe and TaskbarWidgets.zip
```

<details>
<summary><b>Project layout</b></summary>

```
src/
├─ Shared/      taskbar host window, free-space layout, panel UI kit, theme, icons, raw-COM UI Automation
├─ Battery/     Bluetooth / Razer HID / system battery + battery widget & panel
├─ Volume/      Core Audio (master, per-app, output switching) + media sessions + widget & panel
└─ Setup/       installer / uninstaller (both apps are embedded in it as resources)
docs/images/    README screenshots
build.ps1       build script (also renders the .ico files from the same vector icons)
install.ps1     one-line installer (downloads the latest release, checks SHA-256, installs silently)
AGENTS.md       instructions for AI assistants: install for a user, or work on the code (llms.txt sums it up)
```
</details>

<details>
<summary><b>How it works</b></summary>

```mermaid
flowchart LR
    TB[Windows taskbar] -- UI Automation scan --> L[Free-space layout]
    TB -- move events --> L
    L --> W[Layered widget window<br/>owned by the taskbar]
    BT[Bluetooth watcher + PnP battery] --> B[Battery widget]
    RZ[Razer HID] --> B
    GSMTC[Windows media sessions] --> M[Volume & Media widget]
    CA[Core Audio COM] --> M
    B --> W
    M --> W
```

- The widget is a per-pixel-alpha layered window **owned by the taskbar**. Owned windows always stay above their owner, which is the reliable way to stay on top of the Windows 11 taskbar.
- Free space = taskbar width − (Start, search, app buttons, tray) − sibling widget. The widget asks its content to fit that width.
- Panel text is drawn with GDI. GDI ignores Graphics transforms, so the scrolled content is drawn into an offscreen buffer first.
</details>

<details>
<summary><b>Debug switches</b></summary>

| Switch | Effect |
|---|---|
| `--panel N` | Open the panel on tab N at startup |
| `TW_PIN=1` | Keep the panel open when it loses focus |
| `TW_DEBUG=1` | Save the rendered widget bitmap to `%APPDATA%\TaskbarWidgets` |
| `TW_DEMO=1` | Fake devices and media (used for the screenshots), with separate settings |
| `TW_FAKE_VERSION=1.0.0` | Pretend to be an older version to test the updater against the real latest release |

Settings live in `%APPDATA%\TaskbarWidgets\*.ini`, and errors are logged to `*.log` in the same folder.
</details>

---

## ❓ FAQ

<details>
<summary><b>The widget moved somewhere else on its own</b></summary>

It never covers taskbar icons. If the spot you picked has no room left (lots of open apps), it first shrinks, then moves to the largest free gap. Close a few apps, or pick another **Position**, and it comes back.
</details>

<details>
<summary><b>Will it spam me with notifications?</b></summary>

No. The low-battery alert fires **once per discharge** and only re-arms after the device has been charged. It's remembered even across restarts. Connect/disconnect popups are **off by default** (the widget blinks instead), and dongle mice that just fell asleep never trigger anything.
</details>

<details>
<summary><b>Can it leave my music app too quiet?</b></summary>

No. While it adjusts the music app, the widget saves your real volume to disk. Close the widget, or even force-close it, and the next start puts the volume straight back.
</details>

<details>
<summary><b>I use two monitors</b></summary>

Turn on *Settings → Personalization → Taskbar → Show my taskbar on all displays*, then pick the display in the widget's **General → Display** setting.
</details>

<details>
<summary><b>Does it connect to the internet?</b></summary>

Only to check for updates: one request a day to the GitHub API (`api.github.com/repos/W1nOfGood/taskbar-widgets/releases/latest`), plus the download when you install an update. Nothing about you or your devices is sent. Turn it off in **General → Updates → Check for updates**.
</details>

<details>
<summary><b>How do I remove it?</b></summary>

Turn off *Start with Windows*, exit from the right-click menu, and delete the folder (plus `%APPDATA%\TaskbarWidgets` if you want the settings gone too).
</details>

---

<div align="center">

Made with ☕ for a cleaner taskbar · [MIT License](LICENSE)

</div>
