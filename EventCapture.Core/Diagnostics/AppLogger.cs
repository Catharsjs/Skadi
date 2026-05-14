namespace EventCapture.Core.Diagnostics;

// Мінімальний логер для діагностики помилок
// Файл: Documents\EventCapture\app.log
public static class AppLogger
{
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EventCapture", "app.log");

    private static readonly object _lock = new();

    public static void Log(string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public static void LogError(string context, string error) =>
        Log($"ERROR [{context}] — {error}");
}