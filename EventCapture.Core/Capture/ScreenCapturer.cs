using EventCapture.Core.Diagnostics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
namespace EventCapture.Core.Capture;

// COM-interop для створення GraphicsCaptureItem
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
interface IGraphicsCaptureItemInterop
{
    // Keep the native vtable order even though window capture is not supported.
    IntPtr CreateForWindow(
        [In] IntPtr window,
        [In] ref Guid iid);

    IntPtr CreateForMonitor(
        [In] IntPtr monitor,
        [In] ref Guid iid);
}

// COM-interop для отримання DXGI texture з WinRT surface
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

// Захоплення екрана (WGC)
public class ScreenCapturer : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoolDelegate(IntPtr thisPtr, byte value);
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly object ScreenshotWgcDeviceSync = new();
    private static SharpDX.Direct3D11.Device? _screenshotD3dDevice;
    private static IDirect3DDevice? _screenshotWinRtDevice;
    private readonly VideoEncoder _encoder;
    private readonly int _fps;
    private readonly string _captureTarget;
    private readonly object _captureLock = new();
    private volatile bool _isRunning;
    private SharpDX.Direct3D11.Device? _d3dDevice;
    private IDirect3DDevice? _wrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SharpDX.Direct3D11.Texture2D? _stagingTexture;
    private int _textureWidth;
    private int _textureHeight;
    private byte[]? _frameBuffer;
    private Task? _captureTask;
    private CancellationTokenSource? _captureCts;

    public bool IsRunning => _isRunning;
    public bool HasFirstFrame { get; private set; }
    private long _framesCaptured;
    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public ScreenCapturer(
        VideoEncoder encoder,
        int fps = 15,
        string captureTarget = "PrimaryMonitor")
    {
        _encoder = encoder;
        _fps = fps;
        _captureTarget = captureTarget;
    }

    // Запуск capture pipeline (...    
    public void Start()
    {
        if (_isRunning)
            return;

        _captureCts = new CancellationTokenSource();

        InitializeDevices();

        var captureItem = CreateCaptureItem(_captureTarget);

        _framePool =
            Direct3D11CaptureFramePool.CreateFreeThreaded(
                _wrtDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                captureItem.Size);

        _session = _framePool.CreateCaptureSession(captureItem);
        _session.IsCursorCaptureEnabled = false;
        TryDisableYellowBorder(_session);

        _session.StartCapture();
        HasFirstFrame = false;
        _isRunning = true;
        _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token));
        AppLogger.Info("Screen capture started");
    }

    private void InitializeDevices()
    {
        _d3dDevice = new SharpDX.Direct3D11.Device(
            SharpDX.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);

        _wrtDevice = CreateDirect3DDevice(_d3dDevice);
    }

    // ...) Запуск capture pipeline


    // Capture loop (...    

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long frameIntervalTicks = Math.Max(1, Stopwatch.Frequency / _fps);
        long nextFrameTicks = stopwatch.ElapsedTicks;

        while (_isRunning &&
               !cancellationToken.IsCancellationRequested)
        {
            long nowTicks = stopwatch.ElapsedTicks;
            long waitTicks = nextFrameTicks - nowTicks;

            if (waitTicks > 0)
            {
                double waitMilliseconds = waitTicks * 1000.0 / Stopwatch.Frequency;
                await WaitForNextFrame(waitMilliseconds, stopwatch, cancellationToken);
            }

            if (_isRunning &&
                !cancellationToken.IsCancellationRequested)
            {
                CaptureFrame();
            }

            nextFrameTicks += frameIntervalTicks;
            nowTicks = stopwatch.ElapsedTicks;

            if (nextFrameTicks < nowTicks - frameIntervalTicks)
            {
                nextFrameTicks = nowTicks + frameIntervalTicks;
            }
        }
    }

    private async Task WaitForNextFrame(
        double waitMs,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (waitMs > 2)
        {
            try
            {
                await Task.Delay((int)(waitMs - 1), cancellationToken);
            }
            catch (TaskCanceledException) {}
        }
        else if (waitMs > 0)
        {
            double spinTarget = stopwatch.Elapsed.TotalMilliseconds + waitMs;

            while (_isRunning &&
                   !cancellationToken.IsCancellationRequested &&
                   stopwatch.Elapsed.TotalMilliseconds < spinTarget)
            {
                Thread.SpinWait(10);
            }
        }
    }
    // ...) Capture loop

    // Захоплення кадру (... 
    private void CaptureFrame()
    {
        if (_framePool == null || _d3dDevice == null)
        {
            return;
        }

        lock (_captureLock)
        {
            if (!_isRunning || _framePool == null || _d3dDevice == null)
            {
                return;
            }

            try
            {
                using var frame = _framePool.TryGetNextFrame();

                if (frame == null)
                    return;

                if (!HasFirstFrame)
        HasFirstFrame = true;
        Interlocked.Increment(ref _framesCaptured);

                using var texture = GetFrameTexture(frame);
                var description = texture.Description;

                EnsureStagingTexture(description.Width, description.Height);
                _d3dDevice.ImmediateContext.CopyResource(texture, _stagingTexture!);
                CopyFrameToManagedBuffer(description);
                _encoder.WriteFrame(
                    _frameBuffer!,
                    description.Width,
                    description.Height);
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(ScreenCapturer), $"CaptureFrame error: {ex}");
            }
        }
    }

    private static SharpDX.Direct3D11.Texture2D GetFrameTexture(
     Direct3D11CaptureFrame frame)
    {
        IDirect3DDxgiInterfaceAccess? surfaceAccess = null;

        try
        {
            surfaceAccess =
                frame.Surface.As<IDirect3DDxgiInterfaceAccess>();

            var textureGuid =
                typeof(SharpDX.Direct3D11.Texture2D).GUID;

            IntPtr texturePointer =
                surfaceAccess.GetInterface(ref textureGuid);

            if (texturePointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Failed to obtain DXGI texture from capture frame.");
            }

            // Texture2D приймає володіння COM-посиланням.
            // Його звільнить using var texture у CaptureFrame().
            return new SharpDX.Direct3D11.Texture2D(
                texturePointer);
        }
        finally
        {
            if (surfaceAccess is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else if (surfaceAccess != null &&
                     Marshal.IsComObject(surfaceAccess))
            {
                Marshal.FinalReleaseComObject(
                    surfaceAccess);
            }
        }
    }

    private void CopyFrameToManagedBuffer(Texture2DDescription description)
    {
        var mappedResource =
            _d3dDevice!.ImmediateContext.MapSubresource(
                _stagingTexture!,
                0,
                MapMode.Read,
                SharpDX.Direct3D11.MapFlags.None);

        try
        {
            int stride = description.Width * 4;
            int requiredSize = stride * description.Height;
            EnsureFrameBuffer(requiredSize);

            for (int y = 0; y < description.Height; y++)
            {
                var source =
                    IntPtr.Add(
                        mappedResource.DataPointer,
                        y * mappedResource.RowPitch);

                Marshal.Copy(
                    source,
                    _frameBuffer!,
                    y * stride,
                    stride);
            }
        }
        finally
        {
            _d3dDevice.ImmediateContext.UnmapSubresource(_stagingTexture!, 0);
        }
    }

    private void EnsureFrameBuffer(int requiredSize)
    {
        if (_frameBuffer == null || _frameBuffer.Length != requiredSize)
        {
            _frameBuffer = new byte[requiredSize];
        }
    }

    // ...) Захоплення кадру


    // Staging texture (...    

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != null && _textureWidth == width && _textureHeight == height)
        {
            return;
        }

        _stagingTexture?.Dispose();
        _stagingTexture =
            new SharpDX.Direct3D11.Texture2D(
                _d3dDevice!,
                new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                });

        _textureWidth = width;
        _textureHeight = height;
    }
    // ...) Staging texture

    // Вимкнення жовтої рамки WGC (...    
    private static void TryDisableYellowBorder(GraphicsCaptureSession session)
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return;

        try
        {
            var sessionAbi =
                ((IWinRTObject)session)
                .NativeObject
                .ThisPtr;

            var session3Guid =
                new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be");

            Marshal.QueryInterface(
                sessionAbi,
                in session3Guid,
                out var session3Pointer);

            if (session3Pointer == IntPtr.Zero)
                return;

            try
            {
                var vtable = Marshal.ReadIntPtr(session3Pointer);
                var setterPointer = Marshal.ReadIntPtr( vtable, 7 * IntPtr.Size);
                var setter = Marshal.GetDelegateForFunctionPointer<SetBoolDelegate>(setterPointer); setter(session3Pointer, 0);
            }
            finally
            {
                Marshal.Release(session3Pointer);
            }
        }
        catch {}
    }
    // ...) Вимкнення жовтої рамки WGC

    // Створення WinRT device (...    
    private static IDirect3DDevice CreateDirect3DDevice(SharpDX.Direct3D11.Device device)
    {
        using var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
        int result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var unknownPointer);

        if (result != 0)
        {
            throw new Exception($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{result:X8}");
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(unknownPointer);
        }
        finally
        {
            Marshal.Release(unknownPointer);
        }
    }
    // ...) Створення WinRT device

    // Створення GraphicsCaptureItem (...
    private static GraphicsCaptureItem CreateCaptureItem(string captureTarget)
    {
        var monitorHandle = ResolveMonitorHandle(NormalizeCaptureTarget(captureTarget));
        string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        WindowsCreateString(
            className,
            className.Length,
            out var hstring);

        try
        {
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            int result = RoGetActivationFactory(hstring, ref interopGuid, out var factoryPointer);

            if (result != 0)
            {
                throw new Exception($"RoGetActivationFactory failed: 0x{result:X8}");
            }

            IGraphicsCaptureItemInterop? interop = null;
            try
            {
                interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
                var itemGuid = GraphicsCaptureItemGuid;
                var itemPointer = interop.CreateForMonitor(monitorHandle, ref itemGuid);

                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer)
                        ?? throw new Exception("Failed to create GraphicsCaptureItem");
                }
                finally
                {
                    if (itemPointer != IntPtr.Zero)
                    {
                        Marshal.Release(itemPointer);
                    }
                }
            }
            finally
            {
                if (interop is not null &&
                    Marshal.IsComObject(interop))
                {
                    Marshal.ReleaseComObject(interop);
                }
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public static (int Width, int Height) GetTargetSize(string captureTarget)
    {
        var monitorScreen = DisplayMonitorService.Resolve(NormalizeCaptureTarget(captureTarget));
        return (monitorScreen.Bounds.Width, monitorScreen.Bounds.Height);
    }

    public static int GetTargetRefreshRate(string captureTarget)
    {
        try
        {
            DisplayMonitor screen = DisplayMonitorService.Resolve(NormalizeCaptureTarget(captureTarget));
            var deviceMode = new DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            if (EnumDisplaySettings(
                    screen.DeviceName,
                    -1,
                    ref deviceMode) &&
                deviceMode.dmDisplayFrequency > 1)
            {
                return deviceMode.dmDisplayFrequency;
            }
        }
        catch
        {
        }

        return 60;
    }

    private static IntPtr ResolveMonitorHandle(string captureTarget)
    {
        DisplayMonitor monitor = DisplayMonitorService.Resolve(captureTarget);

        if (monitor.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The selected monitor is no longer available: {monitor.DeviceName}");
        }

        return monitor.Handle;
    }

    private static string NormalizeCaptureTarget(string captureTarget)
    {
        return captureTarget.StartsWith("Window|", StringComparison.Ordinal)
            ? "PrimaryMonitor"
            : captureTarget;
    }
    // ...) Створення GraphicsCaptureItem

    public static Bitmap? CaptureScreenshot(
        string captureTarget,
        int timeoutMilliseconds = 2000)
    {
        string normalizedTarget = NormalizeCaptureTarget(captureTarget);
        bool useDesktopDuplication = IsWindows10DesktopDuplicationRequired();
        string backend = useDesktopDuplication ? "DDA" : "WGC";

        AppLogger.Info(
            $"Screenshot backend selected | Backend={backend} | Target={normalizedTarget} | WindowsBuild={Environment.OSVersion.Version.Build}");

        return useDesktopDuplication
            ? CaptureScreenshotDesktopDuplication(normalizedTarget, timeoutMilliseconds)
            : CaptureScreenshotWgc(normalizedTarget, timeoutMilliseconds);
    }

    private static bool IsWindows10DesktopDuplicationRequired()
    {
        Version version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() &&
               version.Major == 10 &&
               version.Build < 22000;
    }

    private static Bitmap? CaptureScreenshotDesktopDuplication(
        string captureTarget,
        int timeoutMilliseconds)
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

                            if (description.MonitorHandle != monitor.Handle)
                                continue;

                            AppLogger.Info(
                                $"DDA screenshot output selected | Device={description.DeviceName} | Adapter={adapter.Description1.Description} | Bounds={monitor.Bounds} | Rotation={description.Rotation}");

                            return CaptureDesktopDuplicationOutput(
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
                nameof(ScreenCapturer),
                $"DDA screenshot output was not found | Device={monitor.DeviceName} | Monitor=0x{monitor.Handle.ToInt64():X}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenCapturer),
                $"DDA screenshot failed | Device={monitor.DeviceName} | {ex}");
            return null;
        }
    }

    private static Bitmap? CaptureDesktopDuplicationOutput(
        Adapter1 adapter,
        Output output,
        DisplayModeRotation rotation,
        DisplayMonitor monitor,
        int timeoutMilliseconds)
    {
        const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

        using var device = new SharpDX.Direct3D11.Device(
            adapter,
            DeviceCreationFlags.BgraSupport |
            DeviceCreationFlags.VideoSupport);
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

                if (acquireResult.Code == DxgiErrorWaitTimeout)
                    continue;

                acquireResult.CheckError();

                if (desktopResource is null)
                {
                    throw new InvalidOperationException(
                        "Desktop Duplication returned no desktop resource.");
                }

                frameAcquired = true;

                if (frameInformation.LastPresentTime == 0)
                {
                    AppLogger.Info(
                        $"DDA screenshot frame skipped | Device={monitor.DeviceName} | Reason=NoDesktopPresent | AccumulatedFrames={frameInformation.AccumulatedFrames} | ElapsedMs={stopwatch.ElapsedMilliseconds}");
                    continue;
                }

                using (SharpDX.Direct3D11.Texture2D texture =
                       desktopResource.QueryInterface<SharpDX.Direct3D11.Texture2D>())
                {
                    Texture2DDescription description = texture.Description;
                    Bitmap bitmap = CopyTextureToBitmap(
                        device,
                        texture,
                        description.Width,
                        description.Height,
                        forceOpaqueAlpha: true);

                    ApplyDesktopDuplicationRotation(bitmap, rotation);

                    AppLogger.Info(
                        $"DDA screenshot captured | Device={monitor.DeviceName} | Surface={description.Width}x{description.Height} | Output={bitmap.Width}x{bitmap.Height} | Rotation={rotation} | ElapsedMs={stopwatch.ElapsedMilliseconds}");

                    return bitmap;
                }
            }
            finally
            {
                desktopResource?.Dispose();

                if (frameAcquired)
                {
                    duplication.ReleaseFrame();
                }
            }
        }

        AppLogger.Error(
            nameof(ScreenCapturer),
            $"DDA screenshot timed out | Device={monitor.DeviceName} | TimeoutMs={timeoutMilliseconds}");
        return null;
    }

    private static void ApplyDesktopDuplicationRotation(
        Bitmap bitmap,
        DisplayModeRotation rotation)
    {
        RotateFlipType rotateFlip = rotation switch
        {
            DisplayModeRotation.Rotate90 => RotateFlipType.Rotate90FlipNone,
            DisplayModeRotation.Rotate180 => RotateFlipType.Rotate180FlipNone,
            DisplayModeRotation.Rotate270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };

        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
        {
            bitmap.RotateFlip(rotateFlip);
        }
    }

    private static Bitmap? CaptureScreenshotWgc(
        string captureTarget,
        int timeoutMilliseconds)
    {
        lock (ScreenshotWgcDeviceSync)
        {
            EnsureScreenshotWgcDevice();

            return CaptureScreenshotWgcCore(
                captureTarget,
                timeoutMilliseconds,
                _screenshotD3dDevice!,
                _screenshotWinRtDevice!);
        }
    }

    private static void EnsureScreenshotWgcDevice()
    {
        if (_screenshotD3dDevice is not null &&
            _screenshotWinRtDevice is not null)
        {
            return;
        }

        ReleaseScreenshotCaptureResourcesNoLock();

        var d3dDevice = new SharpDX.Direct3D11.Device(
            SharpDX.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);

        try
        {
            IDirect3DDevice winRtDevice = CreateDirect3DDevice(d3dDevice);
            _screenshotD3dDevice = d3dDevice;
            _screenshotWinRtDevice = winRtDevice;
            AppLogger.Info("WGC screenshot shared device created");
        }
        catch
        {
            d3dDevice.Dispose();
            throw;
        }
    }

    public static void ReleaseScreenshotCaptureResources()
    {
        lock (ScreenshotWgcDeviceSync)
        {
            ReleaseScreenshotCaptureResourcesNoLock();
        }
    }

    private static void ReleaseScreenshotCaptureResourcesNoLock()
    {
        if (_screenshotD3dDevice is null &&
            _screenshotWinRtDevice is null)
        {
            return;
        }

        try
        {
            _screenshotD3dDevice?.ImmediateContext.ClearState();
            _screenshotD3dDevice?.ImmediateContext.Flush();
        }
        catch
        {
        }

        try
        {
            _screenshotWinRtDevice?.Dispose();
        }
        catch
        {
        }

        try
        {
            _screenshotD3dDevice?.Dispose();
        }
        catch
        {
        }

        _screenshotWinRtDevice = null;
        _screenshotD3dDevice = null;
        AppLogger.Info("WGC screenshot shared device released");
    }

    private static Bitmap? CaptureScreenshotWgcCore(
        string captureTarget,
        int timeoutMilliseconds,
        SharpDX.Direct3D11.Device d3dDevice,
        IDirect3DDevice winRtDevice)
    {
        GraphicsCaptureItem? captureItem = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            LogScreenshotNativeMemory("WGC:BeforeCaptureItem");
            captureItem =
                CreateCaptureItem(captureTarget);
            LogScreenshotNativeMemory("WGC:AfterCaptureItem");

            if (captureItem.Size.Width <= 0 ||
                captureItem.Size.Height <= 0)
            {
                return null;
            }

            framePool =
                Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    1,
                    captureItem.Size);
            LogScreenshotNativeMemory("WGC:AfterFramePool");

            session =
                framePool.CreateCaptureSession(
                    captureItem);
            LogScreenshotNativeMemory("WGC:AfterSession");

            session.IsCursorCaptureEnabled = false;

            TryDisableYellowBorder(session);

            session.StartCapture();

            var stopwatch =
                Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds <
                   timeoutMilliseconds)
            {
                using Direct3D11CaptureFrame? frame =
                    framePool.TryGetNextFrame();

                if (frame is null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                using SharpDX.Direct3D11.Texture2D texture =
                    GetFrameTexture(frame);
                LogScreenshotNativeMemory("WGC:FrameAcquired");

                int width = Math.Min(
                    frame.ContentSize.Width,
                    texture.Description.Width);

                int height = Math.Min(
                    frame.ContentSize.Height,
                    texture.Description.Height);

                if (width <= 0 || height <= 0)
                {
                    return null;
                }

                Bitmap bitmap = CopyTextureToBitmap(
                    d3dDevice,
                    texture,
                    width,
                    height);
                LogScreenshotNativeMemory("WGC:AfterGpuReadback");

                return CropWindowToVisibleBounds(
                 bitmap,
                 captureTarget);
            }

            AppLogger.Error(
                nameof(ScreenCapturer),
                $"Screenshot capture timed out: {captureTarget}");

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(ScreenCapturer),
                $"WGC screenshot failed: {ex}");

            return null;
        }
        finally
        {
            try
            {
                session?.Dispose();
            }
            catch
            {
            }

            try
            {
                framePool?.Dispose();
            }
            catch
            {
            }

            try
            {
                if ((object?)captureItem is IDisposable disposableCaptureItem)
                {
                    disposableCaptureItem.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                d3dDevice.ImmediateContext.Flush();
            }
            catch
            {
            }

            LogScreenshotNativeMemory("WGC:AfterResourceDispose");
        }
    }

    private static void LogScreenshotNativeMemory(string stage)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        AppLogger.Info(
            $"Screenshot backend memory | Stage={stage} | WorkingSetBytes={process.WorkingSet64} | PrivateBytes={process.PrivateMemorySize64} | ManagedBytes={GC.GetTotalMemory(false)} | Handles={process.HandleCount}");
    }

    private static Bitmap CropWindowToVisibleBounds(
    Bitmap bitmap,
    string captureTarget) => bitmap;

    private static Bitmap CopyTextureToBitmap(
        SharpDX.Direct3D11.Device d3dDevice,
        SharpDX.Direct3D11.Texture2D sourceTexture,
        int width,
        int height,
        bool forceOpaqueAlpha = false)
    {
        Texture2DDescription sourceDescription =
            sourceTexture.Description;

        using var stagingTexture =
            new SharpDX.Direct3D11.Texture2D(
                d3dDevice,
                new Texture2DDescription
                {
                    Width = sourceDescription.Width,
                    Height = sourceDescription.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription =
                        new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                });

        d3dDevice.ImmediateContext.CopyResource(
            sourceTexture,
            stagingTexture);

        DataBox mappedTexture =
            d3dDevice.ImmediateContext.MapSubresource(
                stagingTexture,
                0,
                MapMode.Read,
                SharpDX.Direct3D11.MapFlags.None);

        var bitmap =
            new Bitmap(
                width,
                height,
                PixelFormat.Format32bppArgb);

        BitmapData? bitmapData = null;

        try
        {
            bitmapData =
                bitmap.LockBits(
                    new Rectangle(
                        0,
                        0,
                        width,
                        height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

            int rowBytes = width * 4;
            var rowBuffer = new byte[rowBytes];
            int sampledPixels = 0;
            int sampledTransparentPixels = 0;
            int sampledNonBlackPixels = 0;

            for (int y = 0; y < height; y++)
            {
                IntPtr sourceRow =
                    IntPtr.Add(
                        mappedTexture.DataPointer,
                        y * mappedTexture.RowPitch);

                IntPtr destinationRow =
                    IntPtr.Add(
                        bitmapData.Scan0,
                        y * bitmapData.Stride);

                Marshal.Copy(
                    sourceRow,
                    rowBuffer,
                    0,
                    rowBytes);

                if (forceOpaqueAlpha)
                {
                    for (int x = 0; x < rowBytes; x += 256)
                    {
                        sampledPixels++;
                        if (rowBuffer[x + 3] == 0)
                            sampledTransparentPixels++;
                        if (rowBuffer[x] != 0 ||
                            rowBuffer[x + 1] != 0 ||
                            rowBuffer[x + 2] != 0)
                        {
                            sampledNonBlackPixels++;
                        }
                    }

                    // DXGI desktop duplication does not guarantee meaningful alpha.
                    // Some Windows 10 drivers return zero, making a valid RGB frame
                    // fully transparent when consumed by WPF or encoded as PNG.
                    for (int x = 3; x < rowBytes; x += 4)
                    {
                        rowBuffer[x] = byte.MaxValue;
                    }
                }

                Marshal.Copy(
                    rowBuffer,
                    0,
                    destinationRow,
                    rowBytes);
            }

            if (forceOpaqueAlpha)
            {
                AppLogger.Info(
                    $"DDA screenshot pixels normalized | Format={sourceDescription.Format} | Samples={sampledPixels} | TransparentBefore={sampledTransparentPixels} | NonBlackRgb={sampledNonBlackPixels}");
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (bitmapData is not null)
            {
                bitmap.UnlockBits(bitmapData);
            }

            d3dDevice.ImmediateContext.UnmapSubresource(
                stagingTexture,
                0);
        }
    }

    // Завершення capture (...    
    public void Stop()
    {
        if (!_isRunning &&
            _session == null &&
            _framePool == null &&
            _d3dDevice == null)
        {
            return;
        }

        _isRunning = false;
        HasFirstFrame = false;

        try
        {
            _captureCts?.Cancel();
        }
        catch {}

        try
        {
            _captureTask?.Wait(1000);
        }
        catch {}

        _captureTask = null;

        try
        {
            _captureCts?.Dispose();
        }
        catch {}

        _captureCts = null;

        lock (_captureLock)
        {
            DisposeCaptureResources();
            _textureWidth = 0;
            _textureHeight = 0;
            _frameBuffer = null;
        }

        AppLogger.Info("Screen capture stopped");
    }

    private void DisposeCaptureResources()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;

        try { _framePool?.Dispose(); } catch { }
        _framePool = null;

        try { _stagingTexture?.Dispose(); } catch { }
        _stagingTexture = null;

        try { _wrtDevice?.Dispose(); } catch { }
        _wrtDevice = null;

        try { _d3dDevice?.Dispose(); } catch { }
        _d3dDevice = null;
    }

    public void Dispose()
    {
        Stop();
    }
    // ...) Завершення capture

    // WinAPI (...    
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(
        IntPtr hstring);




    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string deviceName,
        int modeNum,
        ref DEVMODE deviceMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
    // ...) WinAPI
}
