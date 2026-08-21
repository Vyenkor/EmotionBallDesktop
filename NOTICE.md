# Third-party notices

## emotion-ball

Emotionball-Deskpet is a derivative work based on:

- Project: `emotion-ball`
- Author: `sam70361`
- Source: <https://github.com/sam70361/emotion-ball>
- Copyright: Copyright (c) 2026 sam70361
- License: the repository `LICENSE` (non-commercial learning and exchange license) and `LICENSE-COMMERCIAL.md`

The upstream SVG character, animation engine, emotion definitions, gallery, and related assets are retained and adapted. The Windows desktop host, Codex bridge, local activity integration, status bubble, tray integration, persistence, and packaging are modifications made in this derivative repository.

## Microsoft WebView2

The Windows host uses the Microsoft.Web.WebView2 NuGet package. The WebView2 Runtime is supplied by Windows or installed separately by the user and is not bundled in the release archive.

## Node.js

The portable release bundles an official Windows x64 `node.exe` to run the loopback-only local bridge. Its complete license file is included beside the executable at `resources/runtime/LICENSE`.

## Fonts

No PingFangSC font file is included in this repository or its releases. The program uses an installed PingFang font when available and otherwise falls back to Microsoft YaHei UI from Windows.
