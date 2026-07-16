using System.Windows.Media;

namespace EventCapture.App.Models;

public sealed record CapturePreview(
    string Title,
    string Subtitle,
    string TargetValue,
    ImageSource? Preview = null);
