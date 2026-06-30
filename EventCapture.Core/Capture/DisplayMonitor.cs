using System.Drawing;
using System.Runtime.InteropServices;

namespace EventCapture.Core.Capture;

public sealed record DisplayMonitor(
    string DeviceName,
    Rectangle Bounds,
    bool IsPrimary,
    IntPtr Handle);

public static class DisplayMonitorService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;

    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var monitors = new List<DisplayMonitor>();

        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitorHandle, _, _, _) =>
            {
                var info =
                    new MonitorInfoEx
                    {
                        Size = Marshal.SizeOf<MonitorInfoEx>()
                    };

                if (!GetMonitorInfo(
                        monitorHandle,
                        ref info))
                {
                    return true;
                }

                monitors.Add(
                    new DisplayMonitor(
                        info.DeviceName,
                        Rectangle.FromLTRB(
                            info.Monitor.Left,
                            info.Monitor.Top,
                            info.Monitor.Right,
                            info.Monitor.Bottom),
                        (info.Flags & MonitorInfoPrimary) != 0,
                        monitorHandle));

                return true;
            },
            IntPtr.Zero);

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Bounds.Left)
            .ThenBy(monitor => monitor.Bounds.Top)
            .ToList();
    }

    public static DisplayMonitor GetPrimary()
    {
        IReadOnlyList<DisplayMonitor> monitors =
            GetAll();

        return monitors.FirstOrDefault(
                   monitor => monitor.IsPrimary) ??
               monitors.FirstOrDefault() ??
               throw new InvalidOperationException(
                   "No display monitors were found.");
    }

    public static DisplayMonitor Resolve(
        string captureTarget)
    {
        IReadOnlyList<DisplayMonitor> monitors =
            GetAll();

        if (captureTarget.StartsWith(
                "Monitor|",
                StringComparison.Ordinal))
        {
            string deviceName =
                captureTarget["Monitor|".Length..];

            DisplayMonitor? selected =
                monitors.FirstOrDefault(
                    monitor => string.Equals(
                        monitor.DeviceName,
                        deviceName,
                        StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
            {
                return selected;
            }
        }

        return monitors.FirstOrDefault(
                   monitor => monitor.IsPrimary) ??
               monitors.FirstOrDefault() ??
               throw new InvalidOperationException(
                   "No display monitors were found.");
    }

    public static DisplayMonitor FromWindow(
        IntPtr windowHandle)
    {
        IntPtr monitorHandle =
            MonitorFromWindow(
                windowHandle,
                MonitorDefaultToNearest);

        return FromHandle(monitorHandle);
    }

    public static DisplayMonitor FromHandle(
        IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The selected monitor is no longer available.");
        }

        var info =
            new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>()
            };

        if (!GetMonitorInfo(
                monitorHandle,
                ref info))
        {
            throw new InvalidOperationException(
                "The selected monitor is no longer available.");
        }

        return new DisplayMonitor(
            info.DeviceName,
            Rectangle.FromLTRB(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right,
                info.Monitor.Bottom),
            (info.Flags & MonitorInfoPrimary) != 0,
            monitorHandle);
    }

    public static IntPtr MonitorFromPoint(
        int x,
        int y)
    {
        return MonitorFromPoint(
            new NativePoint(x, y),
            MonitorDefaultToNearest);
    }

    private delegate bool MonitorEnumProc(
        IntPtr monitorHandle,
        IntPtr deviceContext,
        IntPtr rectangle,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr windowHandle,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(
        NativePoint point,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(
            int x,
            int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
