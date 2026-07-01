# Skadi

Skadi is a compact Windows desktop capture tool built around a WPF side-panel UI and a native GPU-accelerated recording pipeline.

The application runs in the background and provides three main workflows:

- quick screenshots with area selection;
- instant replay export from a rolling buffer;
- regular start/stop recording.

> Portfolio note: this project demonstrates WPF/MVVM desktop UI development, Windows Graphics Capture integration, Direct3D 11 interop, Media Foundation hardware encoding, audio capture, global hotkeys, DPI-aware layout, installer packaging, and native/managed interoperability.

## Download and Install

The latest installer is included in the repository:

```text
Installer/Output/Skadi_Setup_v2.0.1.exe
```

To install Skadi on another Windows PC:

1. Download or clone this repository.
2. Run `Installer/Output/Skadi_Setup_v2.0.1.exe`.
3. Follow the installer steps.
4. Launch Skadi from the Start Menu, desktop shortcut, or system tray.

The installer targets x64 Windows and automatically downloads and installs the .NET 10 Desktop Runtime if it is missing.

## Default Hotkeys

| Action | Hotkey |
|---|---|
| Save Screenshot | `Alt+F1` |
| Start/Stop Recording | `Alt+F2` |
| Save Replay | `Alt+F3` |
| Show/Hide UI | `Alt+Z` |

Hotkeys can be changed from the UI. A hotkey can also be cleared with `Backspace`, after which it is shown as `Not assigned`.

## Features

- Screenshot capture
  - opens a dimmed multi-monitor selection overlay;
  - click a monitor to capture the full monitor;
  - drag-select an area to capture only that region;
  - saves the screenshot as PNG;
  - copies the screenshot to the clipboard.
- Instant replay buffer
  - keeps the latest configured duration in the background;
  - exports the latest buffer with `Save Replay`;
  - supports video, audio, and combined capture modes.
- Regular recording
  - `Start Recording` / `Stop Recording`;
  - saves a complete recording from the moment recording starts;
  - supports video, audio, and combined capture modes.
- Capture target selection for video recording and replay
  - monitors;
  - Alt+Tab-style windows;
  - preview grid with pagination.
- Audio controls
  - system audio device selection;
  - microphone device selection;
  - per-source recording volume;
  - live activity indicators;
  - automatic refresh when audio devices are added, removed, or changed.
- Background-first workflow
  - starts in the background;
  - tray menu;
  - show/hide UI via hotkey;
  - hide button inside the UI.
- DPI-aware WPF UI
  - Per-Monitor DPI Awareness V2;
  - fixed reference composition scaled with a `Viewbox`;
  - consistent proportions across 1080p, 1440p, 4K, and ultrawide displays.

## UI Design

Skadi uses a restrained dark UI with a compact side-panel layout.

Core design principles:

- no decorative gradients;
- no nested card-heavy layout;
- centralized WPF resource dictionaries;
- MVVM bindings and commands;
- smooth state transitions;
- scalable reference layout.

Main palette:

| Token | Color |
|---|---|
| Background | `#1C1C1E` |
| Surface | `#2A2A2E` |
| Hover | `#323236` |
| Border | `#3A3A3E` |
| Accent | `#00C4A0` |
| Danger | `#DC5050` |
| Primary text | `#F0F0F0` |
| Secondary text | `#969696` |

## Architecture

Skadi is split into three main parts:

```text
EventCapture.App
  WPF UI, MVVM, tray integration, global hotkeys, notifications

EventCapture.Core
  capture coordinator, audio recorder, screenshot selection, settings-facing services

EventCapture.Native
  native C++ GPU video pipeline:
  Windows Graphics Capture -> Direct3D 11 -> GPU BGRA/NV12 conversion -> hardware H.264 encoder
```

### High-level video pipeline

```text
Selected monitor/window
        ↓
Windows Graphics Capture
        ↓
Direct3D 11 texture
        ↓
GPU color conversion / frame normalization
        ↓
Media Foundation H.264 hardware encoder
        ↓
Bounded encoded replay storage or continuous recording writer
        ↓
MP4 output
```

The video pipeline avoids copying full frames into managed memory. Encoded samples are stored in bounded native storage for replay, which keeps memory usage predictable during background sessions.

### Screenshot pipeline

```text
Save Screenshot / Alt+F1
        ↓
Multi-monitor selection overlay
        ↓
Full monitor click or region selection
        ↓
Windows Graphics Capture screenshot
        ↓
PNG file + clipboard
```

Screenshot capture is intentionally independent from the selected video capture target.

## Technology Stack

- C#
- .NET 10
- WPF
- XAML
- MVVM
- C++20
- C++/WinRT
- Windows Graphics Capture
- Direct3D 11
- Media Foundation hardware H.264 encoding
- NAudio / WASAPI
- FFmpeg
- Inno Setup
- Per-Monitor DPI Awareness V2

## Requirements for Running

- Windows 10 19041+ or Windows 11
- x64 system
- .NET 10 Desktop Runtime
  - installed automatically by the installer if missing

## Requirements for Development

- Visual Studio 2026 with:
  - .NET desktop development workload;
  - Desktop development with C++;
  - Windows SDK 10.0.26100.0 or compatible;
  - MSVC toolset compatible with `v145`.
- x64 build target.
- Inno Setup 6 for installer builds.

## Build from Source

Open:

```text
EventCapture.slnx
```

Recommended configuration:

```text
Debug | x64
```

or:

```text
Release | x64
```

Then run:

```text
Rebuild Solution
```

Rebuild is recommended because the WPF app depends on the native `EventCapture.Native.dll`. The app project copies the native DLL into the output directory after build.

## Build Installer

Publish the WPF application for x64:

```powershell
MSBuild EventCapture.App\EventCapture.App.csproj /t:Restore,Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=false
```

Then compile the installer:

```powershell
ISCC Installer\Skadi.iss
```

The installer output is written to:

```text
Installer/Output/Skadi_Setup_v2.0.1.exe
```

## Project Structure

```text
EventCapture.App/
  WPF application, XAML views, view models, styles, tray and hotkey services

EventCapture.Core/
  capture coordination, audio capture, screenshot overlay integration, FFmpeg integration

EventCapture.Native/
  C++ native GPU video engine and exported C ABI

Installer/
  Inno Setup installer script and installer output

ThirdParty/FFmpeg/
  bundled FFmpeg binaries and license files
```

## Current Status

Implemented:

- WPF UI integrated with capture backend;
- monitor/window capture target selection;
- instant replay export;
- regular start/stop recording;
- monitor/region screenshot capture;
- system audio capture;
- microphone capture path;
- configurable hotkeys;
- tray integration;
- native GPU video pipeline;
- hardware H.264 encoding;
- MP4 output;
- DPI-aware scalable side panel;
- Windows installer with .NET runtime bootstrap.

Known limitations:

- Some protected or elevated windows may not be capturable because of OS-level restrictions, DRM, or application-specific rendering behavior.
- Monitor capture currently uses Windows Graphics Capture; performance can vary depending on GPU load, compositor behavior, and the target application.
- Further backend work is planned for more stable high-load monitor/game capture scenarios.

## Screenshots

![Skadi Basic Settings](docs/screenshots/basicsettings.png)
![Skadi Advanced Settings](docs/screenshots/advancedsettings.png)

## License

Copyright © 2026 Catharsjs. All rights reserved.

This repository is public for portfolio and source-code review purposes only.

Commercial use, redistribution, modification, sublicensing, or deployment in an organization is prohibited without explicit written permission from the author.

Third-party components are licensed separately. FFmpeg is distributed under the GNU GPL v3; see `ThirdParty/FFmpeg/LICENSE` and `ThirdParty/FFmpeg/README.txt`.
