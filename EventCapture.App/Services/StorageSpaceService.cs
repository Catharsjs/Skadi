using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace EventCapture.App.Services;

internal static class StorageSpaceService
{
    public static long GetAvailableFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string probePath = NormalizeProbePath(path);
        if (!GetDiskFreeSpaceEx(
                probePath,
                out ulong availableToCaller,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not query available space for {path}.");
        }

        return availableToCaller > long.MaxValue
            ? long.MaxValue
            : (long)availableToCaller;
    }

    public static void EnsureAvailable(
        string path,
        long requiredBytes,
        string message)
    {
        long availableBytes = GetAvailableFreeBytes(path);
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"{message} Available={availableBytes}; Required={requiredBytes}.");
        }
    }

    private static string NormalizeProbePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return fullPath.TrimEnd('\\') + '\\';

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new DirectoryNotFoundException(
                $"Could not determine the storage root for {path}.");
        return root;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
