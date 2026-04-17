using EventCapture.Core.Buffer;
using System.Drawing;
using System.Drawing.Imaging;

namespace EventCapture.Core.Capture;

public class ScreenCapturer : IDisposable
{
    private readonly RingBuffer<FrameEntry> _buffer;
    private System.Threading.Timer? _timer;
    private bool _isRunning;
    private readonly int _fps;

    public bool IsRunning => _isRunning;

    public ScreenCapturer(RingBuffer<FrameEntry> buffer, int fps = 10)
    {
        _buffer = buffer;
        _fps = fps;
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        int interval = 1000 / _fps;
        _timer = new System.Threading.Timer(_ => CaptureFrame(), null, 0, interval);
    }

    public void Stop()
    {
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void CaptureFrame()
    {
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

            using var ms = new System.IO.MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            var frameData = ms.ToArray();

            _buffer.Write(new FrameEntry(frameData, DateTime.Now));
        }
        catch { /* ігноруємо поодинокі помилки захоплення */ }
    }

    public void Dispose()
    {
        Stop();
    }
}