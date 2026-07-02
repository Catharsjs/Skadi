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

        try
        {
            selection = await SelectRegionAsync();

            if (selection is null)
            {
                return null;
            }

            await Task.Delay(80);

            using Bitmap? monitorBitmap =
                ScreenCapturer.CaptureScreenshot(
                    selection.CaptureTarget);

            if (monitorBitmap is null)
            {
                return string.Empty;
            }

            using Bitmap finalBitmap =
                CreateFinalBitmap(
                    monitorBitmap,
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
            if (restoreUiAfterCapture)
            {
                await toggleUi();
            }
        }
    }

    private static async Task<ScreenshotSelectionResult?> SelectRegionAsync()
    {
        var completion =
            new TaskCompletionSource<ScreenshotSelectionResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var windows =
            DisplayMonitorService.GetAll()
                .Select(
                    screen =>
                        new ScreenshotSelectionWindow(
                            screen,
                            result => completion.TrySetResult(result)))
                .Cast<WpfWindow>()
                .ToList();

        try
        {
            foreach (WpfWindow window in windows)
            {
                window.Show();
            }

            ScreenshotSelectionResult? result =
                await completion.Task;

            return result;
        }
        finally
        {
            foreach (WpfWindow window in windows)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                }
            }
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
