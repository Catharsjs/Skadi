using System.Diagnostics;

namespace EventCapture.Core.Capture;

public static class FfmpegLocator
{
    public static string GetFfmpegPath() => FindExecutable("ffmpeg.exe");

    public static string GetFfprobePath() => FindExecutable("ffprobe.exe");

    private static string FindExecutable(string fileName)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, "ThirdParty", "FFmpeg", "bin", fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThirdParty", "FFmpeg", "bin", fileName)
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (string directory in pathEnvironment.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"{fileName} was not found. Expected it near Skadi.exe or in ThirdParty\\FFmpeg\\bin.",
            fileName);
    }

    public static async Task RunAsync(string arguments, string operation)
    {
        string ffmpegPath = GetFfmpegPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {error}");
    }
}
