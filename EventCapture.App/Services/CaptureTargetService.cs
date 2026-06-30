using System.Diagnostics;
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
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint GaRootOwner = 3;
    private const int DwmwaCloaked = 14;
    private const uint PwRenderFullContent = 2;

    public Task<IReadOnlyList<CapturePreview>> GetMonitorsAsync() => Task.Run(() =>
    {
        var result = new List<CapturePreview>();
        int index = 1;
        foreach (DisplayMonitor screen in DisplayMonitorService.GetAll())
        {
            string target = screen.IsPrimary ? "PrimaryMonitor" : $"Monitor|{screen.DeviceName}";
            string title = screen.IsPrimary ? $"Display {index} В· Primary" : $"Display {index}";
            string subtitle = $"{screen.Bounds.Width} Г— {screen.Bounds.Height}";
            result.Add(new CapturePreview(
                $"monitor-{index}", title, subtitle, "в–Ј", target,
                CaptureMonitorPreview(screen)));
            index++;
        }
        return (IReadOnlyList<CapturePreview>)result;
    });

    public Task<IReadOnlyList<CapturePreview>> GetWindowsAsync() => Task.Run(() =>
    {
        var result = new List<CapturePreview>();
        IntPtr shell = GetShellWindow();
        int index = 1;
        EnumWindows((handle, _) =>
        {
            if (handle == shell || !IsAltTabWindow(handle)) return true;
            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == Environment.ProcessId) return true;

            int length = GetWindowTextLength(handle);
            if (length <= 0) return true;
            var titleBuilder = new StringBuilder(length + 1);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            string title = titleBuilder.ToString().Trim();
            if (title.Length == 0) return true;

            string processName = string.Empty;
            try { processName = Process.GetProcessById((int)processId).ProcessName; } catch { }
            string target = $"Window|{handle.ToInt64():X}|{title}";
            result.Add(new CapturePreview(
                $"window-{index++}", title,
                string.IsNullOrWhiteSpace(processName) ? "Window" : processName,
                "в—‡", target, CaptureWindowPreview(handle)));
            return true;
        }, IntPtr.Zero);

        return (IReadOnlyList<CapturePreview>)result
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    });

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

    private static ImageSource? CaptureWindowPreview(IntPtr handle)
    {
        try
        {
            if (!GetWindowRect(handle, out var bounds)) return null;
            int width = Math.Max(1, bounds.Right - bounds.Left);
            int height = Math.Max(1, bounds.Bottom - bounds.Top);
            using var source = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(source))
            {
                IntPtr dc = graphics.GetHdc();
                try { PrintWindow(handle, dc, PwRenderFullContent); }
                finally { graphics.ReleaseHdc(dc); }
            }
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

    private static bool IsAltTabWindow(IntPtr handle)
    {
        if (!IsWindowVisible(handle)) return false;
        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((style & WsExToolWindow) != 0 || (style & WsExNoActivate) != 0) return false;
        int cloaked = 0;
        if (DwmGetWindowAttribute(handle, DwmwaCloaked, ref cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        IntPtr root = GetAncestor(handle, GaRootOwner);
        IntPtr popup = GetLastActivePopup(root);
        while (popup != IntPtr.Zero && popup != root && !IsWindowVisible(popup))
        {
            root = popup;
            popup = GetLastActivePopup(root);
        }
        return popup == handle;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
}

