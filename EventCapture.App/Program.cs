namespace EventCapture.App;

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        SetProcessDPIAware();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}