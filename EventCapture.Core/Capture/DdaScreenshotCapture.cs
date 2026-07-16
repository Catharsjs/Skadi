using System.Diagnostics;
using System.Drawing;
using EventCapture.Core.Diagnostics;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace EventCapture.Core.Capture;

internal static class DdaScreenshotCapture
{
    // Захоплення скріншота через Desktop Duplication API ...
    public static Bitmap? Capture(string captureTarget, int timeoutMilliseconds)
    {
        DisplayMonitor monitor = DisplayMonitorService.Resolve(captureTarget);

        try
        {
            using var factory = new Factory1();
            foreach (Adapter1 adapter in factory.Adapters1)
            {
                using (adapter)
                {
                    foreach (Output output in adapter.Outputs)
                    {
                        using (output)
                        {
                            OutputDescription description = output.Description;
                            if (description.MonitorHandle != monitor.Handle) continue;

                            AppLogger.Info(
                                $"DDA screenshot output selected | Device={description.DeviceName} | " +
                                $"Adapter={adapter.Description1.Description} | Bounds={monitor.Bounds} | " +
                                $"Rotation={description.Rotation}");
                            return CaptureOutput(
                                adapter,
                                output,
                                description.Rotation,
                                monitor,
                                timeoutMilliseconds);
                        }
                    }
                }
            }

            AppLogger.Error(
                nameof(DdaScreenshotCapture),
                $"DDA screenshot output was not found | Device={monitor.DeviceName} | " +
                $"Monitor=0x{monitor.Handle.ToInt64():X}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(DdaScreenshotCapture),
                $"DDA screenshot failed | Device={monitor.DeviceName} | {ex}");
            return null;
        }
    }
    // ...Захоплення скріншота через Desktop Duplication API

    private static Bitmap? CaptureOutput(
        Adapter1 adapter,
        Output output,
        DisplayModeRotation rotation,
        DisplayMonitor monitor,
        int timeoutMilliseconds)
    {
        const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        using var device = new SharpDX.Direct3D11.Device(
            adapter,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using Output1 output1 = output.QueryInterface<Output1>();
        using OutputDuplication duplication = output1.DuplicateOutput(device);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            bool frameAcquired = false;
            SharpDX.DXGI.Resource? desktopResource = null;
            try
            {
                int remainingMilliseconds = Math.Max(
                    1,
                    timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
                SharpDX.Result acquireResult = duplication.TryAcquireNextFrame(
                    Math.Min(100, remainingMilliseconds),
                    out OutputDuplicateFrameInformation frameInformation,
                    out desktopResource);

                if (acquireResult.Code == DxgiErrorWaitTimeout) continue;
                acquireResult.CheckError();
                if (desktopResource is null)
                    throw new InvalidOperationException(
                        "Desktop Duplication returned no desktop resource.");

                frameAcquired = true;
                if (frameInformation.LastPresentTime == 0)
                {
                    AppLogger.Info(
                        $"DDA screenshot frame skipped | Device={monitor.DeviceName} | " +
                        $"Reason=NoDesktopPresent | AccumulatedFrames={frameInformation.AccumulatedFrames} | " +
                        $"ElapsedMs={stopwatch.ElapsedMilliseconds}");
                    continue;
                }

                using var texture =
                    desktopResource.QueryInterface<SharpDX.Direct3D11.Texture2D>();
                Texture2DDescription description = texture.Description;
                Bitmap bitmap = ScreenCapturer.CopyTextureToBitmap(
                    device,
                    texture,
                    description.Width,
                    description.Height,
                    forceOpaqueAlpha: true);
                ApplyRotation(bitmap, rotation);
                AppLogger.Info(
                    $"DDA screenshot captured | Device={monitor.DeviceName} | " +
                    $"Surface={description.Width}x{description.Height} | " +
                    $"Output={bitmap.Width}x{bitmap.Height} | Rotation={rotation} | " +
                    $"ElapsedMs={stopwatch.ElapsedMilliseconds}");
                return bitmap;
            }
            finally
            {
                desktopResource?.Dispose();
                if (frameAcquired) duplication.ReleaseFrame();
            }
        }

        AppLogger.Error(
            nameof(DdaScreenshotCapture),
            $"DDA screenshot timed out | Device={monitor.DeviceName} | " +
            $"TimeoutMs={timeoutMilliseconds}");
        return null;
    }

    private static void ApplyRotation(Bitmap bitmap, DisplayModeRotation rotation)
    {
        RotateFlipType rotateFlip = rotation switch
        {
            DisplayModeRotation.Rotate90 => RotateFlipType.Rotate90FlipNone,
            DisplayModeRotation.Rotate180 => RotateFlipType.Rotate180FlipNone,
            DisplayModeRotation.Rotate270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
            bitmap.RotateFlip(rotateFlip);
    }
}
