# Skadi

Skadi is a compact Windows desktop capture tool built around a WPF control panel and a native GPU-accelerated recording pipeline.

The project is designed as a background utility for instant replay capture, screenshots, and regular start/stop recording. It focuses on a clean side-panel UI, low-latency capture, hardware encoding, and predictable resource usage.

> Portfolio note: this project demonstrates WPF/MVVM desktop UI development, Windows Graphics Capture integration, Direct3D 11 interop, Media Foundation hardware encoding, audio capture, global hotkeys, DPI-aware layout, and native/managed interoperability.

## Features

- Instant replay buffer
  - continuously keeps the latest configured duration in the background;
  - exports the latest buffer with `Save Replay`;
  - supports video, audio, and combined capture modes.
- Regular recording
  - `Start Recording` / `Stop Recording`;
  - separate hotkey from replay export;
  - saves a complete recording from the moment recording starts.
- Screenshot capture
  - monitor capture;
  - selected window capture;
  - save-folder selection.
- Capture target selection
  - monitors;
  - Alt+Tab-style windows;
  - preview grid with pagination.
- Audio controls
  - system audio device selection;
  - microphone device selection;
  - per-source recording volume;
  - automatic refresh when audio devices are added, removed, or changed.
- Global hotkeys
  - configurable from the UI;
  - hotkeys can be cleared with Backspace and shown as `Not assigned`;
  - default mapping:
    - `Alt+F1` — Save Screenshot;
    - `Alt+F2` — Save Replay;
    - `Alt+F3` — Start/Stop Recording;
    - `Alt+Z` — Toggle UI.
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
  capture coordinator, audio recorder, screenshots, settings-facing services

EventCapture.Native
  native C++ GPU video pipeline:
  Windows Graphics Capture → Direct3D 11 → GPU BGRA/NV12 conversion → hardware H.264 encoder
```

### High-level capture pipeline

```text
Selected monitor/window
        ↓
Windows Graphics Capture
        ↓
Direct3D 11 texture
        ↓
GPU color conversion
        ↓
Media Foundation H.264 hardware encoder
        ↓
Bounded encoded replay storage / regular recording file
        ↓
MP4 muxing with FFmpeg
```

The video pipeline avoids copying full frames into managed memory. Encoded packets are stored in a bounded native ring/spool structure, which keeps memory usage predictable during long background sessions.

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
- NAudio
- FFmpeg
- Per-Monitor DPI Awareness V2

## Requirements

- Windows 10 19041+ / Windows 11
- Visual Studio 2026 with:
  - .NET desktop development workload;
  - Desktop development with C++;
  - Windows SDK 10.0.26100.0 or compatible;
  - MSVC toolset compatible with `v145`.
- x64 build target.

## Build

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

## Project Structure

```text
EventCapture.App/
  WPF application, XAML views, view models, styles, tray and hotkey services

EventCapture.Core/
  capture coordination, audio capture, screenshot saving, FFmpeg integration

EventCapture.Native/
  C++ native GPU video engine and exported C ABI

ThirdParty/FFmpeg/
  bundled FFmpeg binaries and license files
```

## Current Status

Implemented:

- WPF UI prototype integrated with capture backend;
- monitor/window capture target selection;
- instant replay export;
- regular start/stop recording;
- screenshot capture;
- system audio capture;
- microphone capture path;
- configurable hotkeys;
- tray integration;
- native GPU video pipeline;
- hardware H.264 encoding;
- DPI-aware scalable side panel.

In progress / planned:

- deeper performance profiling on more hardware configurations;
- additional encoder options;
- more robust edge-case handling for protected or unavailable windows;
- packaging / installer;
- automated UI tests;
- optional overlay refinements.

## Notes

Some windows may not be capturable because of OS-level protection, DRM, elevated-process isolation, or application-specific rendering behavior. Skadi handles unavailable targets as gracefully as possible, but capture availability ultimately depends on Windows Graphics Capture and the target application.

## License

Copyright © 2026 Catharsjs. All rights reserved.

This repository is public for portfolio and source-code review purposes only.

Commercial use, redistribution, modification, sublicensing, or deployment in an organization is prohibited without explicit written permission from the author.

Third-party components are licensed separately. FFmpeg is distributed under the GNU GPL v3; see `ThirdParty/FFmpeg/LICENSE` and `ThirdParty/FFmpeg/README.txt`.