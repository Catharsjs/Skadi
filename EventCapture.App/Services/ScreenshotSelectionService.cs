using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App.Services;

public sealed class ScreenshotSelectionService : IDisposable
{
    private static long _memorySampleSequence;
    private int _isSelectionActive;
    private int _memoryLogVersion;
    private readonly Dictionary<string, ScreenshotSelectionWindow> _overlayWindows =
        new(StringComparer.OrdinalIgnoreCase);
    public bool IsSelectionActive => Volatile.Read(ref _isSelectionActive) != 0;

    private sealed record FrozenMonitorCapture(
    DisplayMonitor Monitor,
    Bitmap Bitmap);
    // Захоплення екранів, вибір області та збереження скріншота ...
    public async Task<string?> CaptureSelectionAsync(
        string outputFolder,
        Func<Task> toggleUi,
        bool restoreUiAfterCapture,
        CancellationToken cancellationToken,
        bool allowBlockingMemoryCleanup)
    {
        int memoryLogVersion = Interlocked.Increment(ref _memoryLogVersion);
        var stopwatch = Stopwatch.StartNew();
        AppLogger.Info($"Screenshot capture started | OutputFolder={outputFolder} | RestoreUi={restoreUiAfterCapture}");
        LogMemory("Start");

        if (restoreUiAfterCapture)
        {
            AppLogger.Info("Screenshot capture | Hiding Skadi panel before selection");
            await toggleUi();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                await dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Render,
                    cancellationToken);
            }

            await Task.Delay(16, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AppLogger.Info("Screenshot capture | Skadi panel hidden");
        }

        List<FrozenMonitorCapture> frozenCaptures = [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            frozenCaptures = CaptureFrozenScreens();
            AppLogger.Info($"Screenshot capture | Frozen monitors={frozenCaptures.Count}");
            LogMemory("AfterFreezeAll");

            if (frozenCaptures.Count == 0)
            {
                AppLogger.Error(
                    nameof(ScreenshotSelectionService),
                    "Screenshot capture canceled because no monitor frame could be captured.");
                return null;
            }

            ScreenshotSelectionResult? selection = await SelectRegionAsync(frozenCaptures, cancellationToken);
            LogMemory("AfterOverlayClosed");

            if (selection is null)
            {
                AppLogger.Info($"Screenshot capture canceled | ElapsedMs={stopwatch.ElapsedMilliseconds}");
                return null;
            }

            AppLogger.Info($"Screenshot capture | Selection received | Device={selection.DeviceName} | FullScreen={selection.IsFullScreen} | Bounds={selection.SelectionBounds}");

            FrozenMonitorCapture? frozenCapture = frozenCaptures.FirstOrDefault(
                capture => capture.Monitor.DeviceName == selection.DeviceName);

            if (frozenCapture is null)
            {
                AppLogger.Error(
                    nameof(ScreenshotSelectionService),
                    $"Screenshot capture missing frozen monitor | Device={selection.DeviceName} | Frozen={string.Join(",", frozenCaptures.Select(c => c.Monitor.DeviceName))}");
                return string.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();

            LogMemory("BeforeFinalBitmap");
            using Bitmap finalBitmap = CreateFinalBitmap(
                frozenCapture.Bitmap,
                selection);
            LogMemory("AfterFinalBitmap");

            string filePath = OutputFileName.Create(outputFolder, "Screenshot", ".png");
            AppLogger.Info($"Screenshot capture | Saving PNG | Path={filePath} | Size={finalBitmap.Width}x{finalBitmap.Height}");

            finalBitmap.Save(
                filePath,
                ImageFormat.Png);
            LogMemory("AfterPngSave");

            if (cancellationToken.IsCancellationRequested)
            {
                TryDelete(filePath);
                cancellationToken.ThrowIfCancellationRequested();
            }

            AppLogger.Info("Screenshot capture | PNG saved, copying to clipboard");
            CopyToClipboard(
                finalBitmap);
            LogMemory("AfterClipboardPersist");

            AppLogger.Info($"Screenshot capture completed | Path={filePath} | ElapsedMs={stopwatch.ElapsedMilliseconds}");
            return filePath;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info($"Screenshot capture canceled by display topology change | ElapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenshotSelectionService),
                $"Screenshot capture failed after {stopwatch.ElapsedMilliseconds} ms: {ex}");

            throw;
        }
        finally
        {
            int disposedCount = frozenCaptures.Count;
            foreach (FrozenMonitorCapture capture in frozenCaptures)
            {
                capture.Bitmap.Dispose();
            }

            frozenCaptures.Clear();
            AppLogger.Info($"Screenshot capture | Released frozen monitors={disposedCount}");
            LogMemory("AfterFrozenBitmapDispose");

            if (restoreUiAfterCapture)
            {
                AppLogger.Info("Screenshot capture | Restoring Skadi panel");
                await toggleUi();
                AppLogger.Info("Screenshot capture | Skadi panel restored");
            }

            LogMemory("Released");
            SchedulePostScreenshotMemoryWork(memoryLogVersion, allowBlockingMemoryCleanup);
        }
    }
    // ...Захоплення екранів, вибір області та збереження скріншота

    private async Task<ScreenshotSelectionResult?> SelectRegionAsync(
    IReadOnlyList<FrozenMonitorCapture> frozenCaptures,
    CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ScreenshotSelectionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var windows = new List<ScreenshotSelectionWindow>(frozenCaptures.Count);

        foreach (FrozenMonitorCapture capture in frozenCaptures)
        {
            if (!_overlayWindows.TryGetValue(capture.Monitor.DeviceName, out ScreenshotSelectionWindow? window))
            {
                window = new ScreenshotSelectionWindow(capture.Monitor);
                _overlayWindows.Add(capture.Monitor.DeviceName, window);
                AppLogger.Info($"Screenshot selection | Reusable overlay created | Device={capture.Monitor.DeviceName}");
            }

            window.Prepare(
                capture.Bitmap,
                result => completion.TrySetResult(result));
            windows.Add(window);
        }

        AppLogger.Info($"Screenshot selection | Creating overlay windows={windows.Count}");
        LogMemory("AfterOverlayCreated");

        try
        {
            Interlocked.Exchange(ref _isSelectionActive, 1);
            await Task.WhenAll(
                windows.Select(window => window.ShowPreparedAsync()));

            AppLogger.Info("Screenshot selection | Overlay windows shown, waiting for user selection");

            using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(
                () =>
                {
                    AppLogger.Info("Screenshot selection canceled because display topology changed.");
                    completion.TrySetResult(null);
                });

            ScreenshotSelectionResult? result = await completion.Task;
            AppLogger.Info($"Screenshot selection | Completed | HasResult={result is not null}");
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _isSelectionActive, 0);

            foreach (ScreenshotSelectionWindow window in windows)
            {
                try { await window.ResetForReuseAsync(); }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        nameof(ScreenshotSelectionService),
                        $"Screenshot selection window reset failed: {ex}");
                }
            }

            AppLogger.Info("Screenshot selection | Overlay windows hidden and reset");
            LogMemory("AfterOverlayReset");
        }
    }

    // Оновлення reusable overlay після зміни моніторів ...
    public void InvalidateOverlayWindows()
    {
        void CloseWindows()
        {
            foreach (ScreenshotSelectionWindow window in _overlayWindows.Values)
            {
                try { window.Close(); }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        nameof(ScreenshotSelectionService),
                        $"Screenshot reusable overlay close failed: {ex}");
                }
            }

            int count = _overlayWindows.Count;
            _overlayWindows.Clear();
            AppLogger.Info($"Screenshot selection | Reusable overlays invalidated={count}");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            CloseWindows();
        else
            dispatcher.BeginInvoke(CloseWindows);
    }
    // ...Оновлення reusable overlay після зміни моніторів

    public void Dispose()
    {
        InvalidateOverlayWindows();
    }

    private static List<FrozenMonitorCapture> CaptureFrozenScreens()
    {
        var captures = new List<FrozenMonitorCapture>();
        IReadOnlyList<DisplayMonitor> monitors = DisplayMonitorService.GetAll();
        AppLogger.Info($"Screenshot freeze | Monitors detected={monitors.Count}");

        foreach (DisplayMonitor monitor in monitors)
        {
            var stopwatch = Stopwatch.StartNew();
            AppLogger.Info($"Screenshot freeze | Capturing monitor | Device={monitor.DeviceName} | Bounds={monitor.Bounds}");
            LogMemory($"BeforeBackendCapture:{monitor.DeviceName}");

            Bitmap? bitmap = ScreenCapturer.CaptureScreenshot($"Monitor|{monitor.DeviceName}");
            LogMemory($"AfterBackendCapture:{monitor.DeviceName}");

            if (bitmap is null)
            {
                AppLogger.Error(
                    nameof(ScreenshotSelectionService),
                    $"Screenshot freeze failed | Device={monitor.DeviceName} | ElapsedMs={stopwatch.ElapsedMilliseconds}");
                continue;
            }

            captures.Add(new FrozenMonitorCapture(
                monitor,
                bitmap));

            LogMemory($"AfterBackendBitmap:{monitor.DeviceName}");

            AppLogger.Info($"Screenshot freeze | Captured monitor | Device={monitor.DeviceName} | Size={bitmap.Width}x{bitmap.Height} | ElapsedMs={stopwatch.ElapsedMilliseconds}");
        }

        return captures;
    }
    private static Bitmap CreateFinalBitmap(
        Bitmap monitorBitmap,
        ScreenshotSelectionResult selection)
    {
        Rectangle cropBounds =
            selection.IsFullScreen
                ? new Rectangle(
                    0,
                    0,
                    monitorBitmap.Width,
                    monitorBitmap.Height)
                : ToBitmapCropBounds(
                    monitorBitmap,
                    selection);

        if (cropBounds.Width == monitorBitmap.Width &&
            cropBounds.Height == monitorBitmap.Height &&
            cropBounds.Left == 0 &&
            cropBounds.Top == 0)
        {
            return new Bitmap(monitorBitmap);
        }

        return monitorBitmap.Clone(
            cropBounds,
            PixelFormat.Format32bppArgb);
    }

    private static Rectangle ToBitmapCropBounds(
        Bitmap monitorBitmap,
        ScreenshotSelectionResult selection)
    {
        double scaleX =
            (double)monitorBitmap.Width /
            selection.ScreenBounds.Width;

        double scaleY =
            (double)monitorBitmap.Height /
            selection.ScreenBounds.Height;

        int left =
            (int)Math.Round(
                (selection.SelectionBounds.Left -
                 selection.ScreenBounds.Left) *
                scaleX);

        int top =
            (int)Math.Round(
                (selection.SelectionBounds.Top -
                 selection.ScreenBounds.Top) *
                scaleY);

        int width =
            (int)Math.Round(
                selection.SelectionBounds.Width *
                scaleX);

        int height =
            (int)Math.Round(
                selection.SelectionBounds.Height *
                scaleY);

        left = Math.Clamp(
            left,
            0,
            monitorBitmap.Width - 1);

        top = Math.Clamp(
            top,
            0,
            monitorBitmap.Height - 1);

        width = Math.Clamp(
            width,
            1,
            monitorBitmap.Width - left);

        height = Math.Clamp(
            height,
            1,
            monitorBitmap.Height - top);

        return new Rectangle(
            left,
            top,
            width,
            height);
    }

    private static void CopyToClipboard(
        Bitmap bitmap)
    {
        const uint CfDibV5 = 17;
        const uint GmemMoveable = 0x0002;
        const int BitmapV5HeaderSize = 124;
        const int BiBitFields = 3;
        const int LcsSrgb = 0x73524742;

        int destinationStride = checked(bitmap.Width * 4);
        int pixelBytes = checked(destinationStride * bitmap.Height);
        int totalBytes = checked(BitmapV5HeaderSize + pixelBytes);
        IntPtr globalMemory = IntPtr.Zero;
        IntPtr lockedMemory = IntPtr.Zero;
        BitmapData? bitmapData = null;

        try
        {
            AppLogger.Info(
                $"Screenshot clipboard | Writing CF_DIBV5 | Size={bitmap.Width}x{bitmap.Height} | Bytes={totalBytes}");

            globalMemory = GlobalAlloc(
                GmemMoveable,
                new UIntPtr((ulong)totalBytes));

            if (globalMemory == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed for screenshot clipboard data.");

            lockedMemory = GlobalLock(globalMemory);
            if (lockedMemory == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed for screenshot clipboard data.");

            WriteBitmapV5Header(
                lockedMemory,
                bitmap.Width,
                bitmap.Height,
                pixelBytes,
                BiBitFields,
                LcsSrgb);

            bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            if (bitmapData.Stride <= 0)
                throw new InvalidOperationException("Screenshot clipboard bitmap has an unsupported negative stride.");

            byte[] row = new byte[destinationStride];
            IntPtr destinationPixels = IntPtr.Add(lockedMemory, BitmapV5HeaderSize);

            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride),
                    row,
                    0,
                    destinationStride);

                Marshal.Copy(
                    row,
                    0,
                    IntPtr.Add(destinationPixels, y * destinationStride),
                    destinationStride);
            }

            bitmap.UnlockBits(bitmapData);
            bitmapData = null;
            GlobalUnlock(globalMemory);
            lockedMemory = IntPtr.Zero;

            OpenClipboardWithRetry();
            try
            {
                if (!EmptyClipboard())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");

                if (SetClipboardData(CfDibV5, globalMemory) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData(CF_DIBV5) failed.");

                globalMemory = IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }

            AppLogger.Info("Screenshot clipboard | CF_DIBV5 ownership transferred to Windows");
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenshotSelectionService),
                $"Screenshot clipboard failed: {ex}");
            throw;
        }
        finally
        {
            if (bitmapData is not null)
                bitmap.UnlockBits(bitmapData);

            if (lockedMemory != IntPtr.Zero)
                GlobalUnlock(globalMemory);

            if (globalMemory != IntPtr.Zero)
                GlobalFree(globalMemory);
        }
    }

    private static void WriteBitmapV5Header(
        IntPtr destination,
        int width,
        int height,
        int pixelBytes,
        int compression,
        int colorSpace)
    {
        byte[] header = new byte[124];

        WriteInt32(header, 0, 124);
        WriteInt32(header, 4, width);
        WriteInt32(header, 8, -height);
        WriteUInt16(header, 12, 1);
        WriteUInt16(header, 14, 32);
        WriteInt32(header, 16, compression);
        WriteInt32(header, 20, pixelBytes);
        WriteInt32(header, 40, unchecked((int)0x00FF0000));
        WriteInt32(header, 44, 0x0000FF00);
        WriteInt32(header, 48, 0x000000FF);
        WriteInt32(header, 52, unchecked((int)0xFF000000));
        WriteInt32(header, 56, colorSpace);

        Marshal.Copy(header, 0, destination, header.Length);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
    }

    private static void OpenClipboardWithRetry()
    {
        const int attempts = 10;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return;

            if (attempt < attempts)
                Thread.Sleep(20);
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenClipboard failed after retrying.");
    }

    private static void LogMemory(
        string stage)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long sequence = Interlocked.Increment(ref _memorySampleSequence);
        AppLogger.Info(
            $"Screenshot memory | Sample={sequence} | Stage={stage} | WorkingSetBytes={process.WorkingSet64} | PrivateBytes={process.PrivateMemorySize64} | ManagedBytes={GC.GetTotalMemory(forceFullCollection: false)} | Handles={process.HandleCount} | GdiObjects={GetGuiResources(process.Handle, 0)} | UserObjects={GetGuiResources(process.Handle, 1)} | Threads={process.Threads.Count} | Gen0={GC.CollectionCount(0)} | Gen1={GC.CollectionCount(1)} | Gen2={GC.CollectionCount(2)} | OverlayWindowsAlive={ScreenshotSelectionWindow.LiveWindowCount} | ReusableOverlayBitmaps={ScreenshotSelectionWindow.ReusableBitmapCount}");
    }

    private void SchedulePostScreenshotMemoryWork(
        int version,
        bool allowBlockingMemoryCleanup)
    {
        _ = RunPostScreenshotMemoryWorkAsync(
            version,
            allowBlockingMemoryCleanup);
    }

    private async Task RunPostScreenshotMemoryWorkAsync(
        int version,
        bool allowBlockingMemoryCleanup)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        if (version != Volatile.Read(ref _memoryLogVersion))
            return;

        if (allowBlockingMemoryCleanup)
        {
            LogMemory("BeforeGC");
            var stopwatch = Stopwatch.StartNew();

            await Task.Run(
                () =>
                {
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: true,
                        compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: true,
                        compacting: false);
                }).ConfigureAwait(false);

            LogMemory("AfterGC");
            AppLogger.Info(
                $"Screenshot memory cleanup completed | ElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        else
        {
            AppLogger.Info(
                "Screenshot blocking memory cleanup deferred because recording or buffer is active");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1250)).ConfigureAwait(false);
        if (version != Volatile.Read(ref _memoryLogVersion))
            return;

        LogMemory("After2s");

        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        if (version != Volatile.Read(ref _memoryLogVersion))
            return;

        LogMemory("After10s");

        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (version != Volatile.Read(ref _memoryLogVersion))
            return;

        LogMemory("After30s");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenshotSelectionService),
                $"Screenshot canceled file cleanup failed | Path={path} | {ex}");
        }
    }
    [DllImport("user32.dll")]
    private static extern int GetGuiResources(
        IntPtr processHandle,
        int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
}
