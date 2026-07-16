using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EventCapture.App.Models;
using EventCapture.Core.Capture;

namespace EventCapture.App.Services;

public sealed class CaptureTargetService
{
    // Пошук моніторів та створення preview для Capture Target ...
    public Task<IReadOnlyList<CapturePreview>> GetMonitorsAsync() => Task.Run(() =>
    {
        var result = new List<CapturePreview>();
        int index = 1;
        foreach (DisplayMonitor screen in DisplayMonitorService.GetAll())
        {
            string target = screen.IsPrimary ? "PrimaryMonitor" : $"Monitor|{screen.DeviceName}";
            string title = screen.IsPrimary ? $"Display {index} Primary" : $"Display {index}";
            string subtitle = $"{screen.Bounds.Width}x{screen.Bounds.Height}";
            result.Add(new CapturePreview(
                title, subtitle, target,
                CaptureMonitorPreview(screen)));
            index++;
        }
        return (IReadOnlyList<CapturePreview>)result;
    });
    // ...Пошук моніторів та створення preview для Capture Target

    private static ImageSource? CaptureMonitorPreview(DisplayMonitor screen)
    {
        try
        {
            using var source = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using (var graphics = Graphics.FromImage(source))
                graphics.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            return ToImageSource(source);
        }
        catch { return null; }
    }
    private static ImageSource ToImageSource(Bitmap source)
    {
        const int targetWidth = 420;
        const int targetHeight = 236;
        using var preview = new Bitmap(targetWidth, targetHeight);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(System.Drawing.Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
        }
        IntPtr bitmap = preview.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { DeleteObject(bitmap); }
    }
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}

