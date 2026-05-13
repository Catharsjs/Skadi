namespace EventCapture.App;

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        using var mutex = new System.Threading.Mutex(true, "EventCapture_SingleInstance", out bool isNewInstance);

        if (!isNewInstance)
        {
            MessageBox.Show("EventCapture вже запущено.", "EventCapture",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetProcessDPIAware();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        SharpDX.MediaFoundation.MediaFactory.Startup(
            SharpDX.MediaFoundation.MediaFactory.Version, 0);

        Application.Run(new MainForm());

        SharpDX.MediaFoundation.MediaFactory.Shutdown();
    }
}