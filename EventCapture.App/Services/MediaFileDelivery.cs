using EventCapture.Core.Diagnostics;
using System.IO;

namespace EventCapture.App.Services;

public static class MediaFileDelivery
{
    private const long DeliveryReserveBytes =
        512L * 1024 * 1024;

    public static async Task<string> DeliverAsync(
        string localPath,
        string? smbFolder,
        CancellationToken cancellationToken = default,
        Action? deliveryStarting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        if (string.IsNullOrWhiteSpace(smbFolder))
            return localPath;
        if (!File.Exists(localPath))
            throw new FileNotFoundException("The local media file does not exist.", localPath);

        if (!Directory.Exists(smbFolder))
            throw new DirectoryNotFoundException(
                "Storage does not exist.");

        long sourceLength = new FileInfo(localPath).Length;
        long requiredBytes = checked(sourceLength + DeliveryReserveBytes);
        StorageSpaceService.EnsureAvailable(
            smbFolder,
            requiredBytes,
            "Not enough space on the selected storage. " +
            "The file was retained locally.");

        string destinationPath = CreateUniqueDestinationPath(
            smbFolder,
            Path.GetFileName(localPath));
        string temporaryPath = Path.Combine(
            smbFolder,
            $".skadi-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            deliveryStarting?.Invoke();

            await using (var source = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                destination.SetLength(sourceLength);
                destination.Position = 0;
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            long copiedLength = new FileInfo(temporaryPath).Length;
            if (copiedLength != sourceLength)
                throw new IOException(
                    $"Storage copy verification failed ({copiedLength} of {sourceLength} bytes).");

            File.Move(temporaryPath, destinationPath);
            File.Delete(localPath);
            AppLogger.Info(
                $"Media delivered to network storage | Source={localPath} | Destination={destinationPath} | Bytes={sourceLength}");
            return destinationPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            AppLogger.Info(
                $"Storage delivery canceled; local file retained | Local={localPath} | Storage={smbFolder}");
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(temporaryPath);
            AppLogger.Error(
                nameof(MediaFileDelivery),
                $"Storage delivery failed; local file retained | Local={localPath} | Storage={smbFolder} | Error={ex}");
            throw new IOException(
                $"Storage copy failed. The local file was kept at: {localPath}",
                ex);
        }
    }

    private static string CreateUniqueDestinationPath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
            return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

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
}
