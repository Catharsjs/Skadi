using System.Drawing;
using System.Drawing.Imaging;

namespace EventCapture.Core.Capture;

// Зберігає скріншот через GDI з масштабуванням до роздільної здатності відео
public class ScreenshotSaver
{
    private readonly string _outputFolder;
    private readonly int _width;
    private readonly int _height;

    public ScreenshotSaver(string outputFolder, int width = 0, int height = 0)
    {
        _outputFolder = outputFolder;
        _width = width;
        _height = height;
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

        // Масштабуємо до тієї ж роздільної здатності що й відео
        if (_width > 0 && _height > 0 && (_width != bounds.Width || _height != bounds.Height))
        {
            using var scaled = new Bitmap(_width, _height);
            using var sg = Graphics.FromImage(scaled);
            sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            sg.DrawImage(bitmap, 0, 0, _width, _height);
            scaled.Save(filePath, ImageFormat.Jpeg);
        }
        else
        {
            bitmap.Save(filePath, ImageFormat.Jpeg);
        }

        return filePath;
    }
}