using System.Drawing;

namespace EventCapture.App.Services;

internal sealed record ScreenshotSelectionResult(
    string CaptureTarget,
    string DeviceName,
    Rectangle ScreenBounds,
    Rectangle SelectionBounds,
    bool IsFullScreen);
