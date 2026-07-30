using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App.Services;

internal static class SmbConnectionService
{
    private const int NoError = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetworkPath = 53;
    private const int ErrorBadNetworkName = 67;
    private const int ErrorLogonFailure = 1326;
    private const int NetworkNameNotFound = 2310;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ResourceTypeDisk = 1;

    public static async Task<string> ConnectAsync(
        string path,
        Func<string, Task<SmbCredentials?>> requestCredentials)
    {
        string normalizedPath = NormalizeUncPath(path);
        var existingAccess = await Task.Run(
            () => VerifyWriteAccess(normalizedPath));
        if (existingAccess.Success)
        {
            AppLogger.Info(
                $"Network storage selected | Existing Windows session reused | Path={normalizedPath}");
            return normalizedPath;
        }

        string shareRoot = GetShareRoot(normalizedPath);
        StorageExistence existence = await Task.Run(
            () => ProbeStorageExistence(shareRoot));
        if (existence == StorageExistence.Missing)
            throw new DirectoryNotFoundException(
                "Storage does not exist.");

        AppLogger.Info(
            $"Network storage requires authorization | Path={normalizedPath} | " +
            $"Existence={existence} | " +
            $"ExistingAccess={DescribeAccessError(existingAccess.Error)}");
        using SmbCredentials credentials =
            await requestCredentials(shareRoot) ??
            throw new OperationCanceledException("Authorization canceled.");
        await Task.Run(
            () => ConnectWithCredentials(
                shareRoot,
                credentials.UserName,
                credentials.Password));

        var authenticatedAccess = await Task.Run(
            () => VerifyWriteAccess(normalizedPath));
        if (!authenticatedAccess.Success)
            throw CreateStorageAccessException(
                authenticatedAccess.Error ??
                new IOException("Storage write access verification failed."));

        AppLogger.Info(
            $"Network storage selected | Authenticated Windows session created | Path={normalizedPath}");
        return normalizedPath;
    }

    internal static string NormalizeUncPath(string value)
    {
        string path = value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException(@"The network storage path must start with \\.");

        string[] parts = path.Split(
            ['\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new ArgumentException(@"Enter both the server and share, for example \\server\share.");
        if (parts.Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("The network storage path contains invalid characters.");
        return @"\\" + string.Join(@"\", parts);
    }

    private static string GetShareRoot(string path)
    {
        string[] parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return $@"\\{parts[0]}\{parts[1]}";
    }

    private static void ConnectWithCredentials(
        string shareRoot,
        string userName,
        SecureString password)
    {
        IntPtr userNamePointer = IntPtr.Zero;
        IntPtr passwordPointer = IntPtr.Zero;
        try
        {
            userNamePointer = Marshal.StringToHGlobalUni(userName);
            passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            var resource = new NetResource
            {
                Scope = 0,
                Type = ResourceTypeDisk,
                DisplayType = 0,
                Usage = 0,
                RemoteName = shareRoot
            };
            int connectionResult = WNetAddConnection2(
                ref resource,
                passwordPointer,
                userNamePointer,
                0);
            if (connectionResult == ErrorSessionCredentialConflict)
            {
                throw new InvalidOperationException(
                    $"Windows already has a network connection to {shareRoot} under another account. " +
                    "Disconnect the existing connection or use its current account, then try again.");
            }
            if (connectionResult == ErrorAccessDenied)
                throw new UnauthorizedAccessException(
                    "Access denied. The selected account does not have permission to use this storage.");
            if (connectionResult is ErrorBadNetworkPath or ErrorBadNetworkName)
                throw new DirectoryNotFoundException(
                    "Storage does not exist.");
            if (connectionResult is 86 or ErrorLogonFailure)
                throw new Win32Exception(
                    connectionResult,
                    "The user name or password is incorrect.");
            if (connectionResult != NoError)
                throw new Win32Exception(connectionResult, $"Could not connect to {shareRoot}.");
        }
        finally
        {
            if (userNamePointer != IntPtr.Zero)
            {
                int bytes = (userName.Length + 1) * sizeof(char);
                for (int offset = 0; offset < bytes; offset++)
                    Marshal.WriteByte(userNamePointer, offset, 0);
                Marshal.FreeHGlobal(userNamePointer);
            }

            if (passwordPointer != IntPtr.Zero)
                Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
        }
    }

    private static StorageExistence ProbeStorageExistence(string shareRoot)
    {
        string[] parts = shareRoot.Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries);
        string serverName = $@"\\{parts[0]}";
        string shareName = parts[1];
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int result = NetShareGetInfo(
                serverName,
                shareName,
                0,
                out buffer);
            return result switch
            {
                NoError => StorageExistence.Exists,
                ErrorBadNetworkPath or
                    ErrorBadNetworkName or
                    NetworkNameNotFound => StorageExistence.Missing,
                ErrorAccessDenied or
                    ErrorLogonFailure => StorageExistence.Unknown,
                _ => StorageExistence.Unknown
            };
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    private static (bool Success, Exception? Error) VerifyWriteAccess(string path)
    {
        string probePath = Path.Combine(
            path,
            $".skadi-access-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory))
                throw new DirectoryNotFoundException(
                    "The selected storage is not a folder.");

            using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough))
            {
                stream.WriteByte(0x53);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probePath);
            return (true, null);
        }
        catch (Exception ex)
        {
            TryDelete(probePath);
            return (false, ex);
        }
    }

    private static Exception CreateStorageAccessException(Exception error)
    {
        Exception root = error is AggregateException aggregate
            ? aggregate.GetBaseException()
            : error;
        int nativeCode = root.HResult & 0xFFFF;

        if (root is UnauthorizedAccessException ||
            root is Win32Exception { NativeErrorCode: 5 } ||
            nativeCode == 5)
        {
            return new UnauthorizedAccessException(
                "Access denied.",
                root);
        }

        if (root is DirectoryNotFoundException ||
            root is FileNotFoundException ||
            root is Win32Exception { NativeErrorCode: 2 or 3 or 53 or 67 } ||
            nativeCode is 2 or 3 or 53 or 67)
        {
            return new DirectoryNotFoundException(
                "Storage does not exist.",
                root);
        }

        return new IOException(
            "Could not access storage.",
            root);
    }

    private static string DescribeAccessError(Exception? error) =>
        error is null
            ? "Write access verified"
            : $"{error.GetType().Name}: {error.Message}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private enum StorageExistence
    {
        Exists,
        Missing,
        Unknown
    }

    internal sealed class SmbCredentials : IDisposable
    {
        public SmbCredentials(string userName, SecureString password)
        {
            UserName = userName;
            Password = password.Copy();
            Password.MakeReadOnly();
        }

        public string UserName { get; }
        public SecureString Password { get; }

        public void Dispose() => Password.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NetResource netResource,
        IntPtr password,
        IntPtr userName,
        int flags);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareGetInfo(
        string serverName,
        string shareName,
        int level,
        out IntPtr buffer);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
