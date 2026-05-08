using EventCapture.Core.Buffer;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EventCapture.Core.Capture;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

public class ScreenCapturer : IDisposable
{
    private readonly RingBuffer<FrameEntry> _buffer;
    private readonly int _fps;
    private bool _isRunning;
    private System.Threading.Timer? _timer;

    private SharpDX.Direct3D11.Device? _d3dDevice;
    private IDirect3DDevice? _wrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    // Перевикористовувані ресурси
    private SharpDX.Direct3D11.Texture2D? _stagingTexture;
    private int _textureWidth;
    private int _textureHeight;

    public bool IsRunning => _isRunning;

    public ScreenCapturer(RingBuffer<FrameEntry> buffer, int fps = 15)
    {
        _buffer = buffer;
        _fps = fps;
    }

    public void Start()
    {
        if (_isRunning) return;

        _d3dDevice = new SharpDX.Direct3D11.Device(
            SharpDX.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);

        _wrtDevice = CreateDirect3DDevice(_d3dDevice);
        var item = CreateCaptureItem();

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _wrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);

        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();

        _isRunning = true;
        int interval = 1000 / _fps;
        _timer = new System.Threading.Timer(_ => CaptureFrame(), null, 0, interval);
    }

    private static IDirect3DDevice CreateDirect3DDevice(SharpDX.Direct3D11.Device device)
    {
        var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pUnknown);
        if (hr != 0) throw new Exception($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
        var result = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
        Marshal.Release(pUnknown);
        return result;
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid GraphicsCaptureItemGuid =
        new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static GraphicsCaptureItem CreateCaptureItem()
    {
        var monitor = System.Windows.Forms.Screen.PrimaryScreen!;
        var hmon = MonitorFromPoint(
            new POINT { x = monitor.Bounds.Left + 1, y = monitor.Bounds.Top + 1 },
            MONITOR_DEFAULTTONEAREST);

        string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hstring);

        try
        {
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = RoGetActivationFactory(hstring, ref interopGuid, out var factoryPtr);
            if (hr != 0) throw new Exception($"RoGetActivationFactory failed: 0x{hr:X8}");

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            var iid = GraphicsCaptureItemGuid;
            var ptr = interop.CreateForMonitor(hmon, ref iid);
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr)
                ?? throw new Exception("Failed to create GraphicsCaptureItem");
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != null &&
            _textureWidth == width &&
            _textureHeight == height) return;

        _stagingTexture?.Dispose();

        var desc = new Texture2DDescription
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
        };

        _stagingTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice!, desc);
        _textureWidth = width;
        _textureHeight = height;
    }

    private int _frameCount = 0;

    private void CaptureFrame()
    {
        if (_framePool == null || _d3dDevice == null) return;

        try
        {
            using var frame = _framePool.TryGetNextFrame();
            if (frame == null) return;

            _frameCount++;

            var texGuid = typeof(SharpDX.Direct3D11.Texture2D).GUID;
            var surfaceNative = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var texPtr = surfaceNative.GetInterface(ref texGuid);
            using var texture = new SharpDX.Direct3D11.Texture2D(texPtr);

            var desc = texture.Description;
            EnsureStagingTexture(desc.Width, desc.Height);

            _d3dDevice.ImmediateContext.CopyResource(texture, _stagingTexture!);

            var mapped = _d3dDevice.ImmediateContext.MapSubresource(
                _stagingTexture!, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

            byte[] jpegBytes;

            try
            {
                var bitmap = new Bitmap(desc.Width, desc.Height, PixelFormat.Format32bppArgb);
                try
                {
                    var bmpData = bitmap.LockBits(
                        new Rectangle(0, 0, desc.Width, desc.Height),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                    for (int y = 0; y < desc.Height; y++)
                    {
                        var src = IntPtr.Add(mapped.DataPointer, y * mapped.RowPitch);
                        var dst = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                        CopyMemory(dst, src, (uint)(desc.Width * 4));
                    }

                    bitmap.UnlockBits(bmpData);

                    using var ms = new MemoryStream();
                    bitmap.Save(ms, ImageFormat.Jpeg);
                    jpegBytes = ms.ToArray();
                }
                finally
                {
                    bitmap.Dispose();
                }
            }
            finally
            {
                _d3dDevice.ImmediateContext.UnmapSubresource(_stagingTexture!, 0);
            }

            _buffer.Write(new FrameEntry(jpegBytes, DateTime.Now));

            if (_frameCount % 50 == 0)
                GC.Collect(0, GCCollectionMode.Optimized, false);
        }
        catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dst, IntPtr src, uint size);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    public void Stop()
    {
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _wrtDevice?.Dispose();
        _wrtDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    public void Dispose() => Stop();
}