using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using EventCapture.Core.Diagnostics;
namespace EventCapture.Core.Capture;

// COM-interop для створення GraphicsCaptureItem
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
interface IGraphicsCaptureItemInterop
{
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
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private readonly VideoEncoder _encoder;
    private readonly int _fps;
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
    public ScreenCapturer(VideoEncoder encoder, int fps = 15)
    {
        _encoder = encoder;
        _fps = fps;
    }

    // Запуск capture pipeline (...    
    public void Start()
    {
        if (_isRunning)
            return;

        _captureCts = new CancellationTokenSource();

        InitializeDevices();

        var captureItem = CreateCaptureItem();

        _framePool =
            Direct3D11CaptureFramePool.CreateFreeThreaded(
                _wrtDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
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
        long frameNumber = 0;
        double frameIntervalMs = 1000.0 / _fps;

        while (_isRunning &&
               !cancellationToken.IsCancellationRequested)
        {
            double targetMs = frameNumber * frameIntervalMs;
            double currentMs = stopwatch.Elapsed.TotalMilliseconds;
            double waitMs = targetMs - currentMs;

            await WaitForNextFrame(
                waitMs,
                stopwatch,
                cancellationToken);

            if (_isRunning &&
                !cancellationToken.IsCancellationRequested)
            {
                CaptureFrame();
            }
            frameNumber++;
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

                using var texture = GetFrameTexture(frame);
                var description = texture.Description;

                EnsureStagingTexture(description.Width, description.Height);
                _d3dDevice.ImmediateContext.CopyResource(texture, _stagingTexture!);
                CopyFrameToManagedBuffer(description);
                _encoder.WriteFrame(_frameBuffer!);
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(ScreenCapturer), $"CaptureFrame error: {ex}");
            }
        }
    }

    private SharpDX.Direct3D11.Texture2D GetFrameTexture(Direct3D11CaptureFrame frame)
    {
        var textureGuid = typeof(SharpDX.Direct3D11.Texture2D).GUID;
        var surface = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var texturePointer = surface.GetInterface(ref textureGuid);
        return new SharpDX.Direct3D11.Texture2D(texturePointer);
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
                ref session3Guid,
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
    private static GraphicsCaptureItem CreateCaptureItem()
    {
        var monitor = Screen.PrimaryScreen!;
        var monitorHandle = MonitorFromPoint(
                new POINT
                {
                    x = monitor.Bounds.Left + 1,
                    y = monitor.Bounds.Top + 1
                },
                MONITOR_DEFAULTTONEAREST);

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

            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
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
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
    // ...) Створення GraphicsCaptureItem

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(
        POINT point,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
    // ...) WinAPI
}