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
public static class ScreenCapturer
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoolDelegate(IntPtr thisPtr, byte value);
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly object ScreenshotWgcDeviceSync = new();
    private static SharpDX.Direct3D11.Device? _screenshotD3dDevice;
    private static IDirect3DDevice? _screenshotWinRtDevice;
    // Запуск capture pipeline ...
    // ...Запуск capture pipeline


    // Capture loop ...

    // ...Capture loop

    // Захоплення кадру ...
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

    // ...Захоплення кадру


    // Staging texture ...

    // ...Staging texture

    // Вимкнення жовтої рамки WGC ...
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
    // ...Вимкнення жовтої рамки WGC

    // Створення WinRT device ...
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
    // ...Створення WinRT device

    // Створення GraphicsCaptureItem ...
    private static GraphicsCaptureItem CreateCaptureItem(string captureTarget)
    {
        var monitorHandle = ResolveMonitorHandle(captureTarget);
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
        var monitorScreen = DisplayMonitorService.Resolve(captureTarget);
        return (monitorScreen.Bounds.Width, monitorScreen.Bounds.Height);
    }

    public static int GetTargetRefreshRate(string captureTarget)
    {
        try
        {
            DisplayMonitor screen = DisplayMonitorService.Resolve(captureTarget);
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

    // ...Створення GraphicsCaptureItem

    // Вибір backend та захоплення скріншота ...
    public static Bitmap? CaptureScreenshot(
        string captureTarget,
        int timeoutMilliseconds = 2000)
    {
        string normalizedTarget = captureTarget;
        bool useDesktopDuplication = IsWindows10DesktopDuplicationRequired();
        string backend = useDesktopDuplication ? "DDA" : "WGC";

        AppLogger.Info(
            $"Screenshot backend selected | Backend={backend} | Target={normalizedTarget} | WindowsBuild={Environment.OSVersion.Version.Build}");

        return useDesktopDuplication
            ? DdaScreenshotCapture.Capture(normalizedTarget, timeoutMilliseconds)
            : CaptureScreenshotWgc(normalizedTarget, timeoutMilliseconds);
    }
    // ...Вибір backend та захоплення скріншота

    private static bool IsWindows10DesktopDuplicationRequired()
    {
        Version version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() &&
               version.Major == 10 &&
               version.Build < 22000;
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

    // Звільнення спільних GPU-ресурсів скріншотів ...
    public static void ReleaseScreenshotCaptureResources()
    {
        lock (ScreenshotWgcDeviceSync)
        {
            ReleaseScreenshotCaptureResourcesNoLock();
        }
    }
    // ...Звільнення спільних GPU-ресурсів скріншотів

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

                return bitmap;
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

    internal static Bitmap CopyTextureToBitmap(
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

    // Завершення capture ...
    // ...Завершення capture

    // WinAPI ...
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
    // ...WinAPI
}
