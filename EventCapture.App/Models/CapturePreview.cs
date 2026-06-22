using System.Windows.Media;

namespace EventCapture.App.Models;

public sealed record CapturePreview(
    string Id,
    string Title,
    string Subtitle,
    string Glyph,
    string TargetValue,
    ImageSource? Preview = null);
