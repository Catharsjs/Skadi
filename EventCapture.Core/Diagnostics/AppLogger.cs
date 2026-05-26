namespace EventCapture.Core.Diagnostics;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EventCapture");
    private static readonly string _logPath = Path.Combine(_logDirectory, "app.log");

    // Інформаційні повідомлення
    public static void Info(string message)
    {
        Write("INFO", message);
    }

    // Повідомлення про помилки
    public static void Error(string context, string error)
    {
        Write("ERROR", $"[{context}] {error}");
    }

    // Debug-повідомлення лише для DEBUG-збірок
    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);

                File.AppendAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Логування не повинно аварійно завершувати роботу застосунку.
            }
        }
    }
}