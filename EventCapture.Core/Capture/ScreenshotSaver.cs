using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
namespace EventCapture.Core.Capture;

// Зберігає скріншоти у роздільній здатності відеопотоку
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

    // Збереження скріншота (...    
    public string SaveScreenshot()
    {
        string fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".jpg";
        string filePath = Path.Combine(_outputFolder, fileName);
        var screenBounds = Screen.PrimaryScreen!.Bounds;

        using var sourceBitmap = new Bitmap(screenBounds.Width, screenBounds.Height);
        using var graphics = Graphics.FromImage(sourceBitmap);

        graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size);

        if (NeedsResize(screenBounds))
        {
            SaveScaledScreenshot(sourceBitmap, filePath);
        }
        else
        {
            sourceBitmap.Save(filePath, ImageFormat.Jpeg);
        }
        return filePath;
    }
    // ...) Збереження скріншота

    // Масштабування зображення (...
    private bool NeedsResize(Rectangle bounds)
    {
        return _width > 0 &&
               _height > 0 &&
               (_width != bounds.Width ||
                _height != bounds.Height);
    }

    private void SaveScaledScreenshot(Bitmap sourceBitmap, string outputPath)
    {
        using var scaledBitmap = new Bitmap(_width, _height);
        using var graphics = Graphics.FromImage(scaledBitmap);

        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.DrawImage(sourceBitmap, 0, 0, _width, _height);
        scaledBitmap.Save(outputPath, ImageFormat.Jpeg);
    }
    // ...) Масштабування зображення
}