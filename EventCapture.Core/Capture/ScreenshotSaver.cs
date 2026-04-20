using EventCapture.Core.Buffer;
using System.Drawing;
using System.Drawing.Imaging;

namespace EventCapture.Core.Capture;

public class ScreenshotSaver
{
    private readonly string _outputFolder;

    public ScreenshotSaver(string outputFolder)
    {
        _outputFolder = outputFolder;
        Directory.CreateDirectory(outputFolder);
    }

    public string SaveScreenshot(RingBuffer<FrameEntry> buffer)
    {
        var frames = buffer.ReadAll();
        if (frames.Length == 0)
            throw new InvalidOperationException("Buffer is empty.");

        var lastFrame = frames[^1];
        return SaveFrameToFile(lastFrame);
    }

    public string SaveCurrentScreen()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        var entry = new FrameEntry(BitmapToBytes(bitmap), DateTime.Now);
        return SaveFrameToFile(entry);
    }

    private string SaveFrameToFile(FrameEntry frame)
    {
        string fileName = frame.Timestamp.ToString("yyyy-MM-dd_HH-mm-ss") + ".jpg";
        string filePath = Path.Combine(_outputFolder, fileName);

        using var ms = new MemoryStream(frame.Data);
        using var bitmap = new Bitmap(ms);
        bitmap.Save(filePath, ImageFormat.Jpeg);

        return filePath;
    }

    private static byte[] BitmapToBytes(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }
}