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

    public string SaveScreenshot()
    {
        string fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".jpg";
        string filePath = Path.Combine(_outputFolder, fileName);

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(filePath, ImageFormat.Jpeg);

        return filePath;
    }
}