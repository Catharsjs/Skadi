using System.Diagnostics;

namespace EventCapture.Core.Capture;

internal static class ReplaySessionStorage
{
    private static readonly string RootDirectory = Path.Combine(Path.GetTempPath(), "SkadiReplay");
    private static int _cleanupStarted;

    public static string CreateSessionDirectory(string prefix)
    {
        Directory.CreateDirectory(RootDirectory);
        CleanupStaleSessionsOnce();

        string path = Path.Combine(
            RootDirectory,
            $"{prefix}_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupStaleSessionsOnce()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
            {
                try
                {
                    bool expired = Directory.GetLastWriteTimeUtc(directory) < cutoff;
                    bool ownerIsGone = !TryGetOwnerProcessId(directory, out int processId) || !IsProcessRunning(processId);
                    if (expired || ownerIsGone)
                        Directory.Delete(directory, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool TryGetOwnerProcessId(string directory, out int processId)
    {
        processId = 0;
        string[] parts = Path.GetFileName(directory).Split('_', 3);
        return parts.Length >= 3 && int.TryParse(parts[1], out processId);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
