# Skadi — WPF integration

This solution combines the Skadi WPF v11 interface with the EventCapture backend.

## Development

1. Open `EventCapture.slnx` in Visual Studio 2026.
2. Select `EventCapture.App` as the startup project.
3. Select the `x64` platform.
4. Build and run the project.

The application starts in the notification area. `Alt+F3` shows or hides the WPF panel.

## Default hotkeys

- `Alt+F1` — save a screenshot.
- `Alt+F2` — save the replay buffer.
- `Alt+F3` — show or hide the UI.

Audio-only mode exports MP3. Combined mode exports MP4 with AAC audio. Audio volume controls change recording gain only and do not change Windows system volume.

## Ready-to-run build

The final package contains a `Run` directory. Start `Run\Skadi.exe`; no Visual Studio launch is required.

FFmpeg and FFprobe are bundled under the terms included in `ThirdParty\FFmpeg\LICENSE`.
