using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EventCapture.App.Services;

public sealed class MonitorPanelSizer(Window window)
{
    private const double ReferenceAspect = 560d / 1400d;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private HwndSource? _source;
    private IntPtr _handle;

    public void Attach()
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        FitToCurrentMonitor();
    }

    public void Detach() => _source?.RemoveHook(WndProc);

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is WmDpiChanged or WmDisplayChange)
            window.Dispatcher.BeginInvoke(FitToCurrentMonitor, DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }

    private void FitToCurrentMonitor()
    {
        if (_handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var workHeightPixels = info.Work.Bottom - info.Work.Top;
        var panelWidthPixels = (int)Math.Round(workHeightPixels * ReferenceAspect);
        var dpi = GetDpiForWindow(_handle);
        var scale = dpi / 96d;

        // Explicit pixel-to-DIP conversion keeps WPF's logical size aligned to PMv2 DPI.
        window.Height = workHeightPixels / scale;
        window.Width = panelWidthPixels / scale;

        // WPF owns sizing in DIPs so the Viewbox receives the correct constraint.
        // Win32 is used only for exact positioning in the monitor's physical work area.
        SetWindowPos(_handle, IntPtr.Zero, info.Work.Right - panelWidthPixels, info.Work.Top,
            0, 0, SwpNoSize | SwpNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
