namespace EventCapture.Core.Diagnostics;

public static class AppLogger
{
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EventCapture", "app.log");

    private static readonly object _lock = new();

    static AppLogger()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        Log("═══════════════════════════════════════");
        Log("EventCapture started");
        Log("═══════════════════════════════════════");
    }

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

    public static void LogSettings(int fps, int bufferSeconds, string saveFolder, int width, int height)
    {
        Log($"SETTINGS — FPS: {fps}, Buffer: {bufferSeconds}s, " +
            $"Resolution: {width}x{height}, Folder: {saveFolder}");
    }

    public static void LogAction(string action)
    {
        Log($"ACTION — {action}");
    }

    public static void LogResult(string result)
    {
        Log($"RESULT — {result}");
    }

    public static void LogError(string context, string error)
    {
        Log($"ERROR [{context}] — {error}");
    }

    public static void LogEncoder(int frameCount, double elapsedMs, double expectedMs)
    {
        Log($"ENCODER — Frame {frameCount}, " +
            $"Last 50 frames: {elapsedMs:F0}ms, Expected: {expectedMs:F0}ms, " +
            $"Ratio: {elapsedMs / expectedMs:F2}x");
    }
}