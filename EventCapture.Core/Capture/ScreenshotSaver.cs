using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace EventCapture.Core.Capture;

public sealed class ScreenshotSaver
{
    private readonly string _outputFolder;
    private readonly int _width;
    private readonly int _height;
    private readonly string _captureTarget;

    public ScreenshotSaver(
        string outputFolder,
        int width = 0,
        int height = 0,
        string captureTarget = "PrimaryMonitor")
    {
        _outputFolder = outputFolder;
        _width = width;
        _height = height;
        _captureTarget = captureTarget;

        Directory.CreateDirectory(outputFolder);
    }

    public string SaveScreenshot()
    {
        string fileName =
            DateTime.Now.ToString(
                "yyyy-MM-dd_HH-mm-ss-fff") +
            ".jpg";

        string filePath =
            Path.Combine(
                _outputFolder,
                fileName);

        Bitmap? sourceBitmap =
            _captureTarget.StartsWith(
                "Window|",
                StringComparison.Ordinal)
                ? ScreenCapturer.CaptureScreenshot(
                    _captureTarget)
                : CaptureMonitor(
                    _captureTarget);

        if (sourceBitmap is null)
        {
            return string.Empty;
        }

        using (sourceBitmap)
        {
            SaveBitmap(
                sourceBitmap,
                filePath);
        }

        return filePath;
    }

    private static Bitmap CaptureMonitor(
        string captureTarget)
    {
        Rectangle bounds =
            ResolveMonitorBounds(
                captureTarget);

        var bitmap =
            new Bitmap(
                bounds.Width,
                bounds.Height,
                PixelFormat.Format32bppArgb);

        using Graphics graphics =
            Graphics.FromImage(bitmap);

        graphics.CopyFromScreen(
            bounds.Location,
            Point.Empty,
            bounds.Size);

        return bitmap;
    }

    private static Rectangle ResolveMonitorBounds(
        string captureTarget)
    {
        if (captureTarget.StartsWith(
                "Monitor|",
                StringComparison.Ordinal))
        {
            string deviceName =
                captureTarget["Monitor|".Length..];

            Screen? selectedScreen =
                Screen.AllScreens.FirstOrDefault(
                    screen => string.Equals(
                        screen.DeviceName,
                        deviceName,
                        StringComparison.OrdinalIgnoreCase));

            if (selectedScreen is not null)
            {
                return selectedScreen.Bounds;
            }

            throw new InvalidOperationException(
                "The selected monitor is no longer available.");
        }

        return Screen.PrimaryScreen?.Bounds
            ?? Screen.AllScreens.First().Bounds;
    }

    private void SaveBitmap(
        Bitmap sourceBitmap,
        string outputPath)
    {
        if (_width <= 0 ||
            _height <= 0 ||
            (_width == sourceBitmap.Width &&
             _height == sourceBitmap.Height))
        {
            sourceBitmap.Save(
                outputPath,
                ImageFormat.Jpeg);

            return;
        }

        using var scaledBitmap =
            new Bitmap(
                _width,
                _height,
                PixelFormat.Format24bppRgb);

        using Graphics graphics =
            Graphics.FromImage(scaledBitmap);

        graphics.Clear(Color.Black);

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;

        double scale = Math.Min(
            (double)_width / sourceBitmap.Width,
            (double)_height / sourceBitmap.Height);

        int scaledWidth =
            (int)Math.Round(
                sourceBitmap.Width * scale);

        int scaledHeight =
            (int)Math.Round(
                sourceBitmap.Height * scale);

        int offsetX =
            (_width - scaledWidth) / 2;

        int offsetY =
            (_height - scaledHeight) / 2;

        graphics.DrawImage(
            sourceBitmap,
            offsetX,
            offsetY,
            scaledWidth,
            scaledHeight);

        scaledBitmap.Save(
            outputPath,
            ImageFormat.Jpeg);
    }
}