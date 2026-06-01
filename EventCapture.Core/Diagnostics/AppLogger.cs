namespace EventCapture.Core.Diagnostics;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Skadi");
    private static readonly string _logPath = Path.Combine(_logDirectory, "app.log");

    // Очистити лог при старті нового сеансу
    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.WriteAllText(_logPath, string.Empty);
        }
        catch { }
    }

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