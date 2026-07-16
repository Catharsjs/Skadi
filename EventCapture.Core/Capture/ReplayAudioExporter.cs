using EventCapture.Core.Diagnostics;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EventCapture.Core.Capture;

internal static class ReplayAudioExporter
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const float LimiterThreshold = 0.95f;
    private const int Mp3Bitrate = 192_000;
    private static readonly WaveFormat MixFormat = new(SampleRate, 16, Channels);

    // Змішування аудіосегментів replay ...
    public static Task MixSegmentsAsync(
        IReadOnlyList<ReplaySegmentBuffer.Segment> segments,
        long windowStart,
        long windowEnd,
        string outputPath) =>
        Task.Run(() => MixSegments(segments, windowStart, windowEnd, outputPath));
    // ...Змішування аудіосегментів replay

    public static bool IsInsideFolder(string path, string folder)
    {
        string fullPath = Path.GetFullPath(path);
        string fullFolder = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    // Об'єднання replay-аудіо з відео ...
    public static Task MuxWithVideoAsync(
        string videoPath,
        string audioPath,
        string outputPath) =>
        Task.Run(() => GpuCapturePipeline.MuxReplayAudio(videoPath, audioPath, outputPath));
    // ...Об'єднання replay-аудіо з відео

    // Кодування replay-аудіо у MP3 ...
    public static Task EncodeMp3Async(string audioPath, string outputPath) =>
        Task.Run(() => EncodeMp3(audioPath, outputPath));
    // ...Кодування replay-аудіо у MP3

    private static void MixSegments(
        IReadOnlyList<ReplaySegmentBuffer.Segment> segments,
        long windowStart,
        long windowEnd,
        string outputPath)
    {
        var inputs = new List<ISampleProvider>();
        var resources = new List<IDisposable>();

        try
        {
            foreach (ReplaySegmentBuffer.Segment segment in segments)
            {
                long overlapStart = Math.Max(windowStart, segment.StartTimestamp);
                long overlapEnd = Math.Min(windowEnd, segment.EndTimestamp);
                if (overlapEnd <= overlapStart || !File.Exists(segment.Path)) continue;

                var reader = new WaveFileReader(segment.Path);
                resources.Add(reader);
                var resampler = new MediaFoundationResampler(reader, MixFormat)
                {
                    ResamplerQuality = 60
                };
                resources.Add(resampler);
                inputs.Add(new OffsetSampleProvider(resampler.ToSampleProvider())
                {
                    SkipOver = TimeSpan.FromMilliseconds(overlapStart - segment.StartTimestamp),
                    Take = TimeSpan.FromMilliseconds(overlapEnd - overlapStart),
                    DelayBy = TimeSpan.FromMilliseconds(overlapStart - windowStart)
                });
            }

            if (inputs.Count == 0) return;

            var mixer = new MixingSampleProvider(inputs) { ReadFully = true };
            long totalFrames = Math.Max(1, (windowEnd - windowStart) * SampleRate / 1000);
            long samplesRemaining = totalFrames * Channels;
            float[] samples = new float[SampleRate * Channels / 10];

            using var writer = new WaveFileWriter(outputPath, MixFormat);
            while (samplesRemaining > 0)
            {
                int requested = (int)Math.Min(samples.Length, samplesRemaining);
                int read = mixer.Read(samples, 0, requested);
                if (read <= 0)
                {
                    Array.Clear(samples, 0, requested);
                    read = requested;
                }

                ApplyLimiter(samples, read);
                writer.WriteSamples(samples, 0, read);
                samplesRemaining -= read;
            }

            AppLogger.Info(
                $"Replay audio snapshot mixed in-process | Segments={inputs.Count} | " +
                $"DurationMs={windowEnd - windowStart} | Format={MixFormat}");
        }
        finally
        {
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                try { resources[index].Dispose(); } catch { }
            }
        }
    }

    private static void ApplyLimiter(float[] samples, int count)
    {
        float peak = 0;
        for (int index = 0; index < count; index++)
            peak = Math.Max(peak, Math.Abs(samples[index]));
        if (peak <= LimiterThreshold) return;

        float gain = LimiterThreshold / peak;
        for (int index = 0; index < count; index++)
            samples[index] *= gain;
    }

    private static void EncodeMp3(string audioPath, string outputPath)
    {
        using var reader = new WaveFileReader(audioPath);
        MediaType? mediaType = MediaFoundationEncoder.SelectMediaType(
            AudioSubtypes.MFAudioFormat_MP3,
            reader.WaveFormat,
            Mp3Bitrate);
        if (mediaType is null)
            throw new InvalidOperationException(
                $"Windows Media Foundation has no MP3 encoder for {reader.WaveFormat}.");

        using var encoder = new MediaFoundationEncoder(mediaType);
        encoder.Encode(outputPath, reader);
        AppLogger.Info(
            $"Replay MP3 encoded in-process | Output={Path.GetFileName(outputPath)} | " +
            $"Format={reader.WaveFormat} | Bitrate={Mp3Bitrate}");
    }
}
