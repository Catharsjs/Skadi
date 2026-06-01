using EventCapture.App;
using EventCapture.Core.Diagnostics;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Захист від повторного запуску (...
        using var mutex = new System.Threading.Mutex(
            true, "EventCapture_SingleInstance", out bool isNewInstance);

        if (!isNewInstance)
        {
            AppLogger.Info("Спроба повторного запуску — застосунок вже працює.");
            MessageBox.Show(
                "Skadi вже запущено.", "Skadi",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // ...) Захист від повторного запуску

        // Ініціалізація застосунку (...
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        AppLogger.Info("Skadi  запускається.");
        // ...) Ініціалізація застосунку

        // Запуск MediaFoundation і головної форми (...
        SharpDX.MediaFoundation.MediaFactory.Startup(
            SharpDX.MediaFoundation.MediaFactory.Version, 0);

        FFMpegCore.GlobalFFOptions.Configure(new FFMpegCore.FFOptions
        {
            BinaryFolder = AppContext.BaseDirectory
        });

        Application.Run(new MainForm());
        SharpDX.MediaFoundation.MediaFactory.Shutdown();
        AppLogger.Info("Skadi завершив роботу.");
        // ...) Запуск MediaFoundation і головної форми
    }
}