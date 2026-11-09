# Taskbar Widgets: instructions for AI assistants

This file is for AI assistants and coding agents (Claude, ChatGPT, Gemini, Copilot, Codex, Cursor and others).
A person probably sent you here and asked you to **install Taskbar Widgets for them**. Everything you need is below.
People reading this: the friendlier guide is the [README](README.md).

## What it is (tell the user this in plain words)

- Two small widgets for the **Windows 11** taskbar (Windows 10 should work but isn't tested):
  - **Battery widget**: battery levels of Bluetooth headphones, earbuds, mice, keyboards, controllers, Razer and Logitech receivers, and the laptop battery.
  - **Volume & Media widget**: what's playing (with album art and controls), a per-app volume mixer, and music that lowers itself during voice calls and lectures.
- Installs **for the current user only**. No admin rights, no extra runtime (it uses .NET Framework 4.8, which is part of Windows).
- Free and open source (MIT). Setup is about 400 KB. Each widget uses about 15 MB of RAM.
- Its only internet use is one daily update check to the GitHub API (it can be turned off in the widget's settings).
- It isn't code-signed. The one-line install below doesn't trigger SmartScreen; the manual download can (the fix is under Troubleshooting).

## 1. Check first

Run this in PowerShell, or ask the user to:

```powershell
[Environment]::OSVersion.Version; [Environment]::Is64BitOperatingSystem
(Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarWidgets -ErrorAction SilentlyContinue).DisplayVersion
```

- It needs Windows 10 or 11 (version `10.0.x`) and `True` for 64-bit. On Windows on ARM it runs through x64 emulation, which hasn't been tested.
- A version number on the last line means it's already installed. Installing again updates or repairs it and keeps the user's choices and settings.

## 2. Install

### If you can run commands (Claude Code, Codex, Gemini CLI, Copilot agent, Cursor, Windsurf...)

Run this in **PowerShell**:

```powershell
irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1 | iex
```

From bash, cmd, Git Bash or WSL:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1 | iex"
```

What [`install.ps1`](install.ps1) does: it downloads `TaskbarWidgetsSetup.exe` from the latest GitHub release and checks its SHA-256 against the digest GitHub publishes. Then it runs the setup silently (`--quiet`) and confirms the app is registered. It takes about 5 to 15 seconds.

- **Success** ends with `Taskbar Widgets X.Y.Z is installed.` and the exit code is 0.
- **Failure** throws a red error that starts with `Taskbar Widgets:` and the exit code is 1. Nothing is half-installed.

By default, setup installs both widgets to `%LOCALAPPDATA%\Programs\Taskbar Widgets`, adds Start menu shortcuts and an "Installed apps" entry, turns on start with Windows, and starts both widgets right away.

If the user wants to choose options (only one widget, no startup, a desktop shortcut, another folder), open the normal setup window instead:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1))) -Window
```

### If you can't run commands (chat assistants)

Give the user these steps:

1. Download the setup: https://github.com/W1nOfGood/taskbar-widgets/releases/latest/download/TaskbarWidgetsSetup.exe
2. Open the downloaded file. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. It shows this because the app isn't code-signed.
3. Click **Install**. Both widgets appear on the taskbar.

Or, if they're comfortable with it, tell them to open **PowerShell** (Start menu, type `PowerShell`, press Enter), paste the one-line command from above and press Enter.

## 3. Verify

```powershell
Get-Process BatteryWidget, VolumeMediaWidget -ErrorAction SilentlyContinue | Select-Object Name, Id
(Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarWidgets).DisplayVersion
```

Two processes and a version number mean it worked. Then tell the user:

- Look at the taskbar. The widgets sit in free space next to the taskbar icons. With auto-hide, they slide in with the taskbar.
- **Click** a widget to open its panel and settings. **Right-click** it for the menu (position, exit).
- On the media widget, **scroll** to change the music app's volume, **Shift + scroll** for all sound, **Ctrl + scroll** for voice chat.
- Devices appear once they're connected. Some Bluetooth devices only report battery a few seconds after connecting.

## 4. Uninstall

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1))) -Uninstall
```

- Add `-DeleteSettings` to also delete the settings in `%APPDATA%\TaskbarWidgets`, but **only if the user asked for that**.
- Without internet, run `"%LOCALAPPDATA%\Programs\Taskbar Widgets\Uninstall.exe" --uninstall --quiet` instead.
- Without a terminal, use **Settings > Apps > Installed apps > Taskbar Widgets > Uninstall**.

The uninstaller closes the widgets gracefully, which also puts back any music volume they had lowered. It removes only files it installed.

## Troubleshooting

| Problem | What to do |
|---|---|
| "Windows protected your PC" | It's SmartScreen, shown because the app isn't code-signed. Click **More info**, then **Run anyway**. The one-line PowerShell install avoids it. |
| Antivirus blocks or deletes the setup | The app is open source and not signed, so some antivirus programs flag it. The user can allow it or build it from source (below). Don't turn antivirus off. |
| `irm` says "could not create SSL/TLS secure channel" | Run `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'` first, then the install command again. |
| `iex` is blocked (company PC, constrained language mode) | Use the manual download steps instead. |
| Installed but no widget on the taskbar | Run `Get-Process BatteryWidget, VolumeMediaWidget`. If there's nothing, open "Battery widget" or "Volume & Media" from the Start menu. If they're running, the taskbar may be full: close a few apps, or right-click the widget and choose another position. |
| A device's battery is missing | It must be connected (not only paired). Some Bluetooth devices never report battery to Windows. The README lists what's supported. |
| Something else | Error logs are in `%APPDATA%\TaskbarWidgets\*.log`. Issues: https://github.com/W1nOfGood/taskbar-widgets/issues |

## Rules for agents

- Only download from `github.com/W1nOfGood/taskbar-widgets` (its releases and `raw.githubusercontent.com/W1nOfGood/taskbar-widgets`). Never use mirrors or re-uploads.
- Don't use admin rights. Never run it elevated (`-Verb RunAs`), and never turn off SmartScreen, Defender or antivirus, or change other security settings.
- Ask before you delete the user's settings (`-DeleteSettings`). A plain install or update never touches them.
- When you're done, tell the user what you installed, where it went, and how to remove it.

---

## Working on the code (coding agents)

Only read this part if the user asked you to change the code itself.

- **Build:** `powershell -ExecutionPolicy Bypass -File build.ps1`. It needs no SDK: it uses `csc.exe` from .NET Framework 4.8, which ships with Windows. Add `-Run` to start the widgets and `-Only battery|volume` for one app. Release files go to `dist\`.
- **Language:** **C# 5** (that compiler's limit). Don't use string interpolation `$"..."`, `?.`, `nameof`, `out var`, expression-bodied members, tuples or pattern matching.
- **No new dependencies:** no NuGet, no WPF, no extra DLLs. Each app is a single exe that must stay around 15 MB of RAM.
- **Layout:** `src/Shared` holds the taskbar window, layout, panel UI kit, updater and version. `src/Battery`, `src/Volume` and `src/Setup` are the two apps and the installer. The version is in `src/Shared/Version.cs`.
- **Privacy:** never commit real device names, Bluetooth or MAC addresses, song titles, user names or user paths. Use `TW_DEMO=1` (fake devices and media) for screenshots.
- **Volume safety:** the media widget changes other apps' volumes. Any code path that lowers a volume must save the user's real volume first and put it back on exit or crash. Never leave the user's music quieter than they set it.
- **Debug switches:** `TW_DEBUG=1` (verbose log plus a debug image), `TW_PIN=1` (the panel stays open), `TW_DEMO=1`, `TW_FAKE_VERSION=1.0.0` (tests the updater), `--panel N`.
