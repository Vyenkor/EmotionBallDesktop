# EmotionBallDesktop

[简体中文](README.md) | [English](README.en.md)

A transparent Windows desktop pet that reacts to Codex and the application you are currently using.

> This is an unofficial Windows desktop derivative of [sam70361/emotion-ball](https://github.com/sam70361/emotion-ball), not a replica or an official release. The upstream project provides the SVG character, emotion definitions, and animation engine. This repository adds the Windows host, Codex bridge, local app awareness, status bubbles, tray integration, and portable packaging.

![EmotionBallDesktop in action](docs/images/desktop-pet-status.png)

[Download the latest Windows release](https://github.com/Vyenkor/EmotionBallDesktop/releases/latest)

## Features

- Codex tasks take priority and show the task name plus thinking, working, searching, waiting, replying, and completion states.
- When Codex is idle, foreground applications are classified locally and mapped to rotating playful phrases and SVG actions.
- After 5 seconds without input the pet enters a sleepy, half-closed-eye idle state; the bubble hides 3 seconds later and wakes to the current app state on mouse movement.
- Three body shapes: blob, wedge, and gem.
- Transparent and always-on-top window, free dragging, and proportional resizing by holding the left mouse button while scrolling.
- Bubble position above or below the pet, with separate Codex and App visibility switches.
- A system tray icon indicates that the local bridge is running and uses the same icon as the executable.
- Single-instance enforcement, persisted position/settings, and screen-edge clamping.
- A dedicated WebView2 data directory plus retry handling for initialization error `0x800700AA`.

## Quick start

1. Download `EmotionBallDesktop-v*-win-x64.zip` from [Releases](https://github.com/Vyenkor/EmotionBallDesktop/releases).
2. Extract the entire archive to a normal folder. Do not run it from inside the ZIP preview.
3. Double-click `EmotionBallDesktop.exe`.
4. Right-click the pet for settings. Exit from either the pet menu or the tray menu.

The portable archive includes the x64 .NET and Node.js runtimes. No Node.js or .NET SDK installation is required. Microsoft Edge WebView2 Runtime is still required and is included with most Windows 10/11 installations.

## Controls

| Input | Action |
| --- | --- |
| Left-button drag | Move the pet with a bounce animation |
| Hold left button + mouse wheel | Resize proportionally |
| Right-click the pet | Open shape, bubble, topmost, and exit settings |
| Double-click the pet | Toggle always-on-top |
| Click the bubble during a Codex task | Reveal the per-task dismiss button |
| Double-click the tray icon | Bring the pet back onto a visible screen area |

## State and privacy

Priority is: `active Codex task > foreground app > idle`.

- The local bridge listens only on `127.0.0.1:8765`.
- Codex integration reads `%USERPROFILE%\.codex\sessions` and the local task title index in read-only mode.
- Chat content, command arguments, tool output, tokens, and credentials are never sent to the web animation layer.
- Window titles, class names, and executable information are classified only inside the local desktop process and are not uploaded.

Codex Desktop is optional. The local app mode continues to work when no Codex task is active.

## Troubleshooting

### The pet does not appear

- Make sure the archive has been fully extracted.
- Check the notification area for the Emotion Ball icon; double-click it to restore the pet.
- Check Task Manager for an existing `EmotionBallDesktop.exe`. Only one instance is allowed.
- Install or repair [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

### `The requested resource is in use (0x800700AA)`

End stale `EmotionBallDesktop.exe` and related `msedgewebview2.exe` processes, verify that `%LOCALAPPDATA%\EmotionBallCodex` is writable, and start the pet again. The app uses `%LOCALAPPDATA%\EmotionBallCodex\WebView2` as a dedicated profile and retries the initialization once.

### Reset position and settings

Exit the pet and delete:

```text
%LOCALAPPDATA%\EmotionBallCodex\window-state.json
```

## Build from source

Requirements: Windows 10/11 x64, .NET 10 SDK, Node.js 18+, and WebView2 Runtime.

```powershell
npm test
dotnet build .\desktop-host\EmotionBallDesktop.csproj -c Release
```

Create the portable release:

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
```

The release script produces a clean root containing only the executable, bilingual documentation, licenses, and a `resources` directory, followed by a ZIP and SHA-256 checksum.

## Repository layout

```text
bridge/          Local Codex state tracker and HTTP/SSE bridge
desktop-host/    WinForms + WebView2 transparent desktop host
js/              emotion-ball engine and desktop-pet frontend
css/             Desktop pet and status-page styles
docs/images/     README screenshots
scripts/         Reproducible release packaging
```

## Upstream, fonts, and licensing

- Upstream: [sam70361/emotion-ball](https://github.com/sam70361/emotion-ball). The original design, SVG engine, emotion data, and related assets remain copyrighted by the upstream author.
- This repository preserves the upstream history, `LICENSE`, `LICENSE-COMMERCIAL.md`, and attribution while clearly identifying desktop-specific changes.
- The upstream learning-and-exchange license applies by default and prohibits commercial use. Contact the upstream author for commercial licensing.
- PingFangSC font files are not redistributed because the referenced font repository does not provide a verifiable redistribution license. An installed PingFang font is used when available; otherwise the app falls back to Microsoft YaHei UI included with Windows.

See the [licensing guide](docs/LICENSING.md) and [third-party notices](NOTICE.md).
