using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using WpfClipboard = System.Windows.Clipboard;
using WpfWindow = System.Windows.Window;

namespace EventCapture.App.Services;

public sealed class ScreenshotSelectionService
{
    private sealed record FrozenMonitorCapture(
    DisplayMonitor Monitor,
    Bitmap Bitmap,
    BitmapSource Image);
    public async Task<string?> CaptureSelectionAsync(
        string outputFolder,
        Func<Task> toggleUi,
        bool restoreUiAfterCapture)
    {
        if (restoreUiAfterCapture)
        {
            await toggleUi();
            await Task.Delay(260);
        }

        ScreenshotSelectionResult? selection = null;
        List<FrozenMonitorCapture> frozenCaptures = CaptureFrozenScreens();

        try
        {
            selection = await SelectRegionAsync(frozenCaptures);

            if (selection is null)
            {
                return null;
            }

            FrozenMonitorCapture? frozenCapture = frozenCaptures.FirstOrDefault(
    capture => capture.Monitor.DeviceName == selection.DeviceName);

            if (frozenCapture is null)
            {
                return string.Empty;
            }

            using Bitmap finalBitmap = CreateFinalBitmap(
                frozenCapture.Bitmap,
                selection);

            string filePath = OutputFileName.Create(outputFolder, "Screenshot", ".png");

            finalBitmap.Save(
                filePath,
                ImageFormat.Png);

            CopyToClipboard(
                finalBitmap);

            return filePath;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenshotSelectionService),
                ex.ToString());

            throw;
        }
        finally
        {
            foreach (FrozenMonitorCapture capture in frozenCaptures)
            {
                capture.Bitmap.Dispose();
            }

            if (restoreUiAfterCapture)
            {
                await toggleUi();
            }
        }
    }

    private static async Task<ScreenshotSelectionResult?> SelectRegionAsync(
    IReadOnlyList<FrozenMonitorCapture> frozenCaptures)
    {
        var completion = new TaskCompletionSource<ScreenshotSelectionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var windows = frozenCaptures
            .Select(capture => new ScreenshotSelectionWindow(
                capture.Monitor,
                capture.Image,
                result => completion.TrySetResult(result)))
            .Cast<WpfWindow>()
            .ToList();

        try
        {
            foreach (WpfWindow window in windows)
            {
                window.Show();
            }

            ScreenshotSelectionResult? result = await completion.Task;
            return result;
        }
        finally
        {
            foreach (WpfWindow window in windows)
            {
                try { window.Close(); }
                catch { }
            }
        }
    }

    private static List<FrozenMonitorCapture> CaptureFrozenScreens()
    {
        var captures = new List<FrozenMonitorCapture>();

        foreach (DisplayMonitor monitor in DisplayMonitorService.GetAll())
        {
            Bitmap? bitmap = ScreenCapturer.CaptureScreenshot($"Monitor|{monitor.DeviceName}");

            if (bitmap is null)
            {
                continue;
            }

            BitmapSource source = CreateBitmapSource(bitmap);

            captures.Add(new FrozenMonitorCapture(
                monitor,
                bitmap,
                source));
        }

        return captures;
    }

    private static BitmapSource CreateBitmapSource(Bitmap bitmap)
    {
        IntPtr bitmapHandle = bitmap.GetHbitmap();

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmapHandle);
        }
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
        IntPtr bitmapHandle =
            bitmap.GetHbitmap();

        try
        {
            BitmapSource source =
                Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            WpfClipboard.SetImage(source);
        }
        finally
        {
            DeleteObject(bitmapHandle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(
        IntPtr objectHandle);
}
