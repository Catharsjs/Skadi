namespace EventCapture.Core.Capture;

public static class OutputFileName
{
    private static readonly object Sync = new();

    public static string Create(
        string folder,
        string prefix,
        string extension,
        DateTime? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        Directory.CreateDirectory(folder);

        string normalizedExtension = extension.StartsWith('.')
            ? extension
            : "." + extension;

        string baseName = $"{prefix} {(timestamp ?? DateTime.Now):yyyy-MM-dd HH-mm-ss}";

        lock (Sync)
        {
            string path = Path.Combine(folder, baseName + normalizedExtension);
            if (!File.Exists(path)) return path;

            for (int index = 2; ; index++)
            {
                string candidate = Path.Combine(
                    folder,
                    $"{baseName} ({index}){normalizedExtension}");

                if (!File.Exists(candidate)) return candidate;
            }
        }
    }
}
