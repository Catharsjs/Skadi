using System.IO;
using System.Runtime.InteropServices;

namespace EventCapture.App.Services;

internal static class StoragePathService
{
    private const uint DriveRemote = 4;

    public static bool IsRemote(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            string fullPath = Path.GetFullPath(expanded);
            string? root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) &&
                   GetDriveType(EnsureTrailingSeparator(root)) == DriveRemote;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ||
        path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}
