using System.Diagnostics;
using System.Globalization;
using EventCapture.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

public sealed class AudioRecorder : IDisposable
{
    private const int SegmentMilliseconds = 2_000;
    private const int GapThresholdMilliseconds = 100;

    private readonly object _systemLock = new();
    private readonly object _microphoneLock = new();
    private readonly ReplaySegmentBuffer _systemSegments;
    private readonly ReplaySegmentBuffer _microphoneSegments;
    private readonly string _sessionDirectory;

    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _microphoneCapture;
    private WaveFileWriter? _systemWriter;
    private WaveFileWriter? _microphoneWriter;
    private WaveFormat? _systemFormat;
    private WaveFormat? _microphoneFormat;
    private string _systemPartialPath = string.Empty;
    private string _microphonePartialPath = string.Empty;
    private long _systemSegmentStart;
    private long _microphoneSegmentStart;
    private long _systemLastPacketEnd;
    private long _microphoneLastPacketEnd;
    private long _systemWrittenBytes;
    private long _microphoneWrittenBytes;
    private long _sharedStartTimestamp;
    private double _systemGain = 1.0;
    private double _microphoneGain = 1.0;
    private WaveFileWriter? _continuousSystemWriter;
    private WaveFileWriter? _continuousMicrophoneWriter;
    private string? _continuousSystemPath;
    private string? _continuousMicrophonePath;
    private long _continuousStartTimestamp;
    private long _continuousSystemWrittenBytes;
    private long _continuousMicrophoneWrittenBytes;
    private bool _continuousRecording;
    private bool _disposed;

    public AudioRecorder(int bufferSeconds = 60)
    {
        TimeSpan retention = TimeSpan.FromSeconds(Math.Max(5, bufferSeconds) + 6);
        _systemSegments = new ReplaySegmentBuffer(retention);
        _microphoneSegments = new ReplaySegmentBuffer(retention);
        _sessionDirectory = ReplaySessionStorage.CreateSessionDirectory("audio");
    }

    public bool IsRecordingSystem { get; private set; }
    public bool IsRecordingMic { get; private set; }
    public bool UseDefaultSystemDevice { get; private set; } = true;
    public bool UseDefaultMicDevice { get; private set; } = true;

    public void SetSystemVolume(double percent) =>
        Volatile.Write(ref _systemGain, Math.Clamp(percent, 0, 100) / 100.0);

    public void SetMicrophoneVolume(double percent) =>
        Volatile.Write(ref _microphoneGain, Math.Clamp(percent, 0, 100) / 100.0);

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => (device.ID, device.FriendlyName))
            .ToList();
    }

    public static List<(string Id, string Name)> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => (device.ID, device.FriendlyName))
            .ToList();
    }

    public void StartRecording(
        bool recordSystem,
        string? systemDeviceId,
        bool recordMicrophone,
        string? microphoneDeviceId,
        long sharedStartTimestamp = 0)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(_sessionDirectory);
        _sharedStartTimestamp = sharedStartTimestamp > 0
            ? sharedStartTimestamp
            : Environment.TickCount64;

        if (recordSystem) StartSystemCapture(systemDeviceId);
        if (recordMicrophone) StartMicrophoneCapture(microphoneDeviceId);
    }

    public void RestartSystemCapture(string? deviceId)
    {
        StopSystemCapture();
        StartSystemCapture(deviceId);
    }

    public void RestartMicCapture(string? deviceId)
    {
        StopMicrophoneCapture();
        StartMicrophoneCapture(deviceId);
    }

    private void StartSystemCapture(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            var capture = new WasapiLoopbackCapture(device);
            _systemCapture = capture;
            _systemFormat = capture.WaveFormat;
            UseDefaultSystemDevice = deviceId is null;

            lock (_systemLock)
            {
                StartSystemWriterCore(_sharedStartTimestamp);
            }

            capture.DataAvailable += (_, args) => HandleSystemPacket(capture.WaveFormat, args.Buffer, args.BytesRecorded);
            capture.StartRecording();
            IsRecordingSystem = true;

            AppLogger.Info(
                $"Segmented system audio started | Device={device.FriendlyName} | " +
                $"Format={capture.WaveFormat}");
        }
        catch (Exception ex)
        {
            IsRecordingSystem = false;
            AppLogger.Error(nameof(AudioRecorder), $"StartSystemCapture failed: {ex}");
        }
    }

    private void StartMicrophoneCapture(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            var capture = new WasapiCapture(device);
            _microphoneCapture = capture;
            _microphoneFormat = capture.WaveFormat;
            UseDefaultMicDevice = deviceId is null;

            lock (_microphoneLock)
            {
                StartMicrophoneWriterCore(_sharedStartTimestamp);
            }

            capture.DataAvailable += (_, args) => HandleMicrophonePacket(capture.WaveFormat, args.Buffer, args.BytesRecorded);
            capture.StartRecording();
            IsRecordingMic = true;

            AppLogger.Info(
                $"Segmented microphone started | Device={device.FriendlyName} | " +
                $"Format={capture.WaveFormat}");
        }
        catch (Exception ex)
        {
            IsRecordingMic = false;
            AppLogger.Error(nameof(AudioRecorder), $"StartMicrophoneCapture failed: {ex}");
        }
    }

    private void HandleSystemPacket(WaveFormat format, byte[] buffer, int count)
    {
        if (count <= 0) return;
        ApplyGain(buffer, count, format, Volatile.Read(ref _systemGain));

        lock (_systemLock)
        {
            if (_systemWriter is null) return;
            long packetEnd = Environment.TickCount64;
            long packetDuration = CalculateBufferDurationMs(format, count);
            long packetStart = packetEnd - packetDuration;

            if (packetStart - _systemSegmentStart >= SegmentMilliseconds)
            {
                long end = Math.Max(_systemSegmentStart + 1, _systemLastPacketEnd);
                FinalizeSystemWriterCore(end, padToEnd: true);
                StartSystemWriterCore(packetStart);
            }

            PadTimelineGap(_systemWriter, format, _systemSegmentStart, _systemWrittenBytes, packetStart, out long paddingBytes);
            _systemWrittenBytes += paddingBytes;
            _systemWriter.Write(buffer, 0, count);
            if (_continuousSystemWriter is not null)
            {
                PadTimelineGap(
                    _continuousSystemWriter,
                    format,
                    _continuousStartTimestamp,
                    _continuousSystemWrittenBytes,
                    packetStart,
                    out long continuousPadding);
                _continuousSystemWrittenBytes += continuousPadding;
                _continuousSystemWriter.Write(buffer, 0, count);
                _continuousSystemWrittenBytes += count;
            }
            _systemWrittenBytes += count;
            _systemLastPacketEnd = packetEnd;
        }
    }

    private void HandleMicrophonePacket(WaveFormat format, byte[] buffer, int count)
    {
        if (count <= 0) return;
        ApplyGain(buffer, count, format, Volatile.Read(ref _microphoneGain));

        lock (_microphoneLock)
        {
            if (_microphoneWriter is null) return;
            long packetEnd = Environment.TickCount64;
            long packetDuration = CalculateBufferDurationMs(format, count);
            long packetStart = packetEnd - packetDuration;

            if (packetStart - _microphoneSegmentStart >= SegmentMilliseconds)
            {
                long end = Math.Max(_microphoneSegmentStart + 1, _microphoneLastPacketEnd);
                FinalizeMicrophoneWriterCore(end, padToEnd: true);
                StartMicrophoneWriterCore(packetStart);
            }

            PadTimelineGap(_microphoneWriter, format, _microphoneSegmentStart, _microphoneWrittenBytes, packetStart, out long paddingBytes);
            _microphoneWrittenBytes += paddingBytes;
            _microphoneWriter.Write(buffer, 0, count);
            if (_continuousMicrophoneWriter is not null)
            {
                PadTimelineGap(
                    _continuousMicrophoneWriter,
                    format,
                    _continuousStartTimestamp,
                    _continuousMicrophoneWrittenBytes,
                    packetStart,
                    out long continuousPadding);
                _continuousMicrophoneWrittenBytes += continuousPadding;
                _continuousMicrophoneWriter.Write(buffer, 0, count);
                _continuousMicrophoneWrittenBytes += count;
            }
            _microphoneWrittenBytes += count;
            _microphoneLastPacketEnd = packetEnd;
        }
    }

    public async Task<string?> SaveLastSecondsAsync(
        string outputFolder,
        int seconds,
        string videoPath,
        long videoElapsedMs,
        long videoStartTimestamp)
    {
        long windowStart = videoStartTimestamp;
        long windowEnd = videoStartTimestamp + Math.Max(1, videoElapsedMs);
        string? mixedAudio = await CreateAudioSnapshotAsync(windowStart, windowEnd);
        if (mixedAudio is null) return null;

        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_final.mp4");

        try
        {
            await MergeWithVideoAsync(videoPath, mixedAudio, outputPath);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        finally
        {
            TryDelete(mixedAudio);
        }
    }

    public async Task<string?> SaveAudioLastSecondsAsMp3Async(string outputFolder, int seconds)
    {
        long windowEnd = Environment.TickCount64;
        long windowStart = Math.Max(_sharedStartTimestamp, windowEnd - Math.Max(1, seconds) * 1000L);
        string? mixedAudio = await CreateAudioSnapshotAsync(windowStart, windowEnd);
        if (mixedAudio is null) return null;

        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_audio.mp3");

        try
        {
            await EncodeMp3Async(mixedAudio, outputPath);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        finally
        {
            TryDelete(mixedAudio);
        }
    }

    public void StartContinuousRecording()
    {
        ThrowIfDisposed();
        if (_continuousRecording)
            throw new InvalidOperationException("Continuous audio recording is already active.");

        _continuousStartTimestamp = Environment.TickCount64;
        _continuousSystemWrittenBytes = 0;
        _continuousMicrophoneWrittenBytes = 0;
        _continuousSystemPath = null;
        _continuousMicrophonePath = null;

        lock (_systemLock)
        {
            if (IsRecordingSystem && _systemFormat is not null)
            {
                _continuousSystemPath = Path.Combine(
                    _sessionDirectory,
                    $"continuous-system-{Guid.NewGuid():N}.wav");
                _continuousSystemWriter = new WaveFileWriter(
                    _continuousSystemPath,
                    _systemFormat);
            }
        }

        lock (_microphoneLock)
        {
            if (IsRecordingMic && _microphoneFormat is not null)
            {
                _continuousMicrophonePath = Path.Combine(
                    _sessionDirectory,
                    $"continuous-microphone-{Guid.NewGuid():N}.wav");
                _continuousMicrophoneWriter = new WaveFileWriter(
                    _continuousMicrophonePath,
                    _microphoneFormat);
            }
        }

        if (_continuousSystemWriter is null && _continuousMicrophoneWriter is null)
            throw new InvalidOperationException("No active audio source is available.");

        _continuousRecording = true;
    }

    public async Task<(string? AudioPath, long StartTimestamp, long EndTimestamp)>
        StopContinuousRecordingAsync()
    {
        if (!_continuousRecording)
            throw new InvalidOperationException("Continuous audio recording is not active.");

        _continuousRecording = false;
        long endTimestamp = Environment.TickCount64;

        lock (_systemLock)
        {
            if (_continuousSystemWriter is not null && _systemFormat is not null)
            {
                PadContinuousWriterToEnd(
                    _continuousSystemWriter,
                    _systemFormat,
                    _continuousStartTimestamp,
                    _continuousSystemWrittenBytes,
                    endTimestamp);
            }
            _continuousSystemWriter?.Flush();
            _continuousSystemWriter?.Dispose();
            _continuousSystemWriter = null;
        }

        lock (_microphoneLock)
        {
            if (_continuousMicrophoneWriter is not null && _microphoneFormat is not null)
            {
                PadContinuousWriterToEnd(
                    _continuousMicrophoneWriter,
                    _microphoneFormat,
                    _continuousStartTimestamp,
                    _continuousMicrophoneWrittenBytes,
                    endTimestamp);
            }
            _continuousMicrophoneWriter?.Flush();
            _continuousMicrophoneWriter?.Dispose();
            _continuousMicrophoneWriter = null;
        }

        var sources = new[] { _continuousSystemPath, _continuousMicrophonePath }
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToArray();

        _continuousSystemPath = null;
        _continuousMicrophonePath = null;

        if (sources.Length == 0)
            return (null, _continuousStartTimestamp, endTimestamp);

        if (sources.Length == 1)
            return (sources[0], _continuousStartTimestamp, endTimestamp);

        string mixedPath = Path.Combine(
            _sessionDirectory,
            $"continuous-mixed-{Guid.NewGuid():N}.wav");
        string arguments =
            $"-y -i \"{sources[0]}\" -i \"{sources[1]}\" " +
            "-filter_complex \"[0:a][1:a]amix=inputs=2:duration=longest:" +
            "dropout_transition=0:normalize=0,alimiter=limit=0.95[out]\" " +
            $"-map \"[out]\" -c:a pcm_s16le \"{mixedPath}\"";

        try
        {
            await RunFfmpegAsync(arguments, "Continuous audio mix");
            return (mixedPath, _continuousStartTimestamp, endTimestamp);
        }
        finally
        {
            foreach (string source in sources) TryDelete(source);
        }
    }

    public static async Task<string> MergeContinuousWithVideoAsync(
        string videoPath,
        string audioPath,
        string outputFolder,
        long videoStartTimestamp,
        long videoEndTimestamp,
        long audioStartTimestamp)
    {
        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_recording.mp4");
        long offsetMilliseconds = audioStartTimestamp - videoStartTimestamp;
        double duration = Math.Max(0.001, (videoEndTimestamp - videoStartTimestamp) / 1000.0);
        string audioFilter = offsetMilliseconds >= 0
            ? $"[1:a]adelay={offsetMilliseconds}:all=1,apad[a]"
            : $"[1:a]atrim=start={FormatSeconds(-offsetMilliseconds / 1000.0)},asetpts=PTS-STARTPTS,apad[a]";
        string arguments =
            $"-y -i \"{videoPath}\" -i \"{audioPath}\" " +
            $"-filter_complex \"{audioFilter}\" " +
            "-map 0:v:0 -map \"[a]\" -c:v copy -c:a aac -b:a 192k " +
            $"-t {FormatSeconds(duration)} -movflags +faststart \"{outputPath}\"";

        AppLogger.Info(
            $"Continuous merge diagnostics | VideoStart={videoStartTimestamp} | " +
            $"VideoEnd={videoEndTimestamp} | AudioStart={audioStartTimestamp} | " +
            $"OffsetMs={offsetMilliseconds} | DurationSec={FormatSeconds(duration)}");

        await RunFfmpegAsync(arguments, "Continuous audio/video merge");
        return outputPath;
    }

    public static async Task<string> EncodeContinuousAudioAsMp3Async(
        string audioPath,
        string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_recording.mp3");
        await EncodeMp3Async(audioPath, outputPath);
        return outputPath;
    }

    private async Task<string?> CreateAudioSnapshotAsync(long windowStart, long windowEnd)
    {
        RotateWritersForSnapshot(Environment.TickCount64);

        using ReplaySegmentBuffer.Lease systemLease = _systemSegments.Acquire(windowStart, windowEnd);
        using ReplaySegmentBuffer.Lease microphoneLease = _microphoneSegments.Acquire(windowStart, windowEnd);

        ReplaySegmentBuffer.Segment[] segments = systemLease.Segments
            .Concat(microphoneLease.Segments)
            .OrderBy(segment => segment.StartTimestamp)
            .ToArray();

        if (segments.Length == 0) return null;

        string outputPath = Path.Combine(_sessionDirectory, $"mix_{Guid.NewGuid():N}.wav");
        await MixSegmentsAsync(segments, windowStart, windowEnd, outputPath);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 78 ? outputPath : null;
    }

    private void RotateWritersForSnapshot(long now)
    {
        lock (_systemLock)
        {
            if (_systemWriter is not null && _systemFormat is not null)
            {
                long end = Math.Min(now, _systemSegmentStart + SegmentMilliseconds);
                end = Math.Max(_systemSegmentStart + 1, Math.Max(end, _systemLastPacketEnd));
                FinalizeSystemWriterCore(end, padToEnd: true);
                StartSystemWriterCore(now);
            }
        }

        lock (_microphoneLock)
        {
            if (_microphoneWriter is not null && _microphoneFormat is not null)
            {
                long end = Math.Min(now, _microphoneSegmentStart + SegmentMilliseconds);
                end = Math.Max(_microphoneSegmentStart + 1, Math.Max(end, _microphoneLastPacketEnd));
                FinalizeMicrophoneWriterCore(end, padToEnd: true);
                StartMicrophoneWriterCore(now);
            }
        }
    }

    private void StartSystemWriterCore(long startTimestamp)
    {
        if (_systemFormat is null) return;
        _systemSegmentStart = startTimestamp;
        _systemLastPacketEnd = startTimestamp;
        _systemWrittenBytes = 0;
        _systemPartialPath = Path.Combine(_sessionDirectory, $"system_{startTimestamp}_{Guid.NewGuid():N}.partial.wav");
        _systemWriter = new WaveFileWriter(_systemPartialPath, _systemFormat);
    }

    private void StartMicrophoneWriterCore(long startTimestamp)
    {
        if (_microphoneFormat is null) return;
        _microphoneSegmentStart = startTimestamp;
        _microphoneLastPacketEnd = startTimestamp;
        _microphoneWrittenBytes = 0;
        _microphonePartialPath = Path.Combine(_sessionDirectory, $"microphone_{startTimestamp}_{Guid.NewGuid():N}.partial.wav");
        _microphoneWriter = new WaveFileWriter(_microphonePartialPath, _microphoneFormat);
    }

    private void FinalizeSystemWriterCore(long endTimestamp, bool padToEnd)
    {
        if (_systemWriter is null || _systemFormat is null) return;
        long padding;
        if (padToEnd) PadWriterToEnd(_systemWriter, _systemFormat, _systemSegmentStart, _systemWrittenBytes, endTimestamp, out padding);
        else padding = 0;
        _systemWrittenBytes += padding;
        _systemWriter.Flush();
        _systemWriter.Dispose();
        _systemWriter = null;
        CommitAudioSegment(_systemPartialPath, _systemSegmentStart, endTimestamp, _systemSegments);
    }

    private void FinalizeMicrophoneWriterCore(long endTimestamp, bool padToEnd)
    {
        if (_microphoneWriter is null || _microphoneFormat is null) return;
        long padding;
        if (padToEnd) PadWriterToEnd(_microphoneWriter, _microphoneFormat, _microphoneSegmentStart, _microphoneWrittenBytes, endTimestamp, out padding);
        else padding = 0;
        _microphoneWrittenBytes += padding;
        _microphoneWriter.Flush();
        _microphoneWriter.Dispose();
        _microphoneWriter = null;
        CommitAudioSegment(_microphonePartialPath, _microphoneSegmentStart, endTimestamp, _microphoneSegments);
    }

    private static void CommitAudioSegment(
        string partialPath,
        long startTimestamp,
        long endTimestamp,
        ReplaySegmentBuffer buffer)
    {
        if (!File.Exists(partialPath) || new FileInfo(partialPath).Length <= 78)
        {
            TryDelete(partialPath);
            return;
        }

        string finalPath = partialPath.Replace(".partial.wav", ".wav", StringComparison.OrdinalIgnoreCase);
        File.Move(partialPath, finalPath, true);
        buffer.Add(finalPath, startTimestamp, Math.Max(startTimestamp + 1, endTimestamp));
        buffer.Prune(endTimestamp);
    }

    private static void PadTimelineGap(
        WaveFileWriter writer,
        WaveFormat format,
        long segmentStart,
        long writtenBytes,
        long packetStart,
        out long paddingBytes)
    {
        long expectedMs = Math.Max(0, packetStart - segmentStart);
        long writtenMs = BytesToMilliseconds(writtenBytes, format);
        long missingMs = expectedMs - writtenMs;
        paddingBytes = missingMs >= GapThresholdMilliseconds
            ? WriteSilence(writer, format, missingMs)
            : 0;
    }

    private static void PadWriterToEnd(
        WaveFileWriter writer,
        WaveFormat format,
        long segmentStart,
        long writtenBytes,
        long endTimestamp,
        out long paddingBytes)
    {
        long expectedMs = Math.Clamp(endTimestamp - segmentStart, 0, SegmentMilliseconds * 2L);
        long writtenMs = BytesToMilliseconds(writtenBytes, format);
        long missingMs = expectedMs - writtenMs;
        paddingBytes = missingMs >= GapThresholdMilliseconds
            ? WriteSilence(writer, format, missingMs)
            : 0;
    }

    private static void PadContinuousWriterToEnd(
        WaveFileWriter writer,
        WaveFormat format,
        long startTimestamp,
        long writtenBytes,
        long endTimestamp)
    {
        long expectedMilliseconds = Math.Max(0, endTimestamp - startTimestamp);
        long writtenMilliseconds = BytesToMilliseconds(writtenBytes, format);
        long missingMilliseconds = expectedMilliseconds - writtenMilliseconds;
        if (missingMilliseconds > 0)
            WriteSilence(writer, format, missingMilliseconds);
    }

    private async Task MixSegmentsAsync(
        IReadOnlyList<ReplaySegmentBuffer.Segment> segments,
        long windowStart,
        long windowEnd,
        string outputPath)
    {
        double windowDuration = Math.Max(0.001, (windowEnd - windowStart) / 1000.0);
        var inputArguments = new List<string>();
        var filters = new List<string>();
        var labels = new List<string>();

        for (int index = 0; index < segments.Count; index++)
        {
            ReplaySegmentBuffer.Segment segment = segments[index];
            long overlapStart = Math.Max(windowStart, segment.StartTimestamp);
            long overlapEnd = Math.Min(windowEnd, segment.EndTimestamp);
            if (overlapEnd <= overlapStart) continue;

            inputArguments.Add($"-i \"{segment.Path}\"");
            double trimStart = Math.Max(0, (overlapStart - segment.StartTimestamp) / 1000.0);
            double trimDuration = Math.Max(0.001, (overlapEnd - overlapStart) / 1000.0);
            long delay = Math.Max(0, overlapStart - windowStart);
            string label = $"a{labels.Count}";
            filters.Add(
                $"[{index}:a]atrim=start={FormatSeconds(trimStart)}:" +
                $"duration={FormatSeconds(trimDuration)}," +
                $"asetpts=PTS-STARTPTS,adelay={delay}:all=1[{label}]");
            labels.Add($"[{label}]");
        }

        if (labels.Count == 0) return;

        string mixedInput = labels.Count == 1
            ? labels[0]
            : string.Concat(labels) +
              $"amix=inputs={labels.Count}:duration=longest:" +
              $"dropout_transition=0:normalize=0,alimiter=limit=0.95[mixed];[mixed]";

        filters.Add(
            $"{mixedInput}apad=pad_dur={FormatSeconds(windowDuration)}," +
            $"atrim=duration={FormatSeconds(windowDuration)}[out]");

        string arguments =
            $"-y {string.Join(' ', inputArguments)} " +
            $"-filter_complex \"{string.Join(';', filters)}\" " +
            $"-map \"[out]\" -c:a pcm_s16le \"{outputPath}\"";

        await RunFfmpegAsync(arguments, "Audio segment mix");
    }

    private static string FormatSeconds(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture);

    private static async Task MergeWithVideoAsync(string videoPath, string audioPath, string outputPath)
    {
        string arguments =
            $"-y -i \"{videoPath}\" -i \"{audioPath}\" " +
            $"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest " +
            $"-movflags +faststart \"{outputPath}\"";
        await RunFfmpegAsync(arguments, "Audio/video merge");
    }

    private static async Task EncodeMp3Async(string audioPath, string outputPath)
    {
        string arguments = $"-y -i \"{audioPath}\" -c:a libmp3lame -q:a 2 \"{outputPath}\"";
        await RunFfmpegAsync(arguments, "MP3 export");
    }

    private static async Task RunFfmpegAsync(string arguments, string operation)
    {
        await FfmpegLocator.RunAsync(arguments, operation);
    }

    private static long CalculateBufferDurationMs(WaveFormat format, int bytesRecorded)
    {
        return format.AverageBytesPerSecond <= 0
            ? 0
            : bytesRecorded * 1000L / format.AverageBytesPerSecond;
    }

    private static long BytesToMilliseconds(long bytes, WaveFormat format)
    {
        return format.AverageBytesPerSecond <= 0
            ? 0
            : bytes * 1000L / format.AverageBytesPerSecond;
    }

    private static long WriteSilence(WaveFileWriter writer, WaveFormat format, long milliseconds)
    {
        long bytes = milliseconds * format.AverageBytesPerSecond / 1000;
        int blockAlign = Math.Max(1, format.BlockAlign);
        bytes -= bytes % blockAlign;
        if (bytes <= 0) return 0;

        long written = 0;
        byte[] buffer = new byte[64 * 1024];
        while (bytes > 0)
        {
            int count = (int)Math.Min(bytes, buffer.Length);
            count -= count % blockAlign;
            if (count <= 0) break;
            writer.Write(buffer, 0, count);
            bytes -= count;
            written += count;
        }

        return written;
    }

    private static void ApplyGain(byte[] buffer, int count, WaveFormat format, double gain)
    {
        if (gain >= 0.9999) return;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int offset = 0; offset + 4 <= count; offset += 4)
            {
                float sample = BitConverter.ToSingle(buffer, offset);
                BitConverter.TryWriteBytes(
                    buffer.AsSpan(offset, 4),
                    (float)Math.Clamp(sample * gain, -1.0, 1.0));
            }
            return;
        }

        if (format.BitsPerSample == 16)
        {
            for (int offset = 0; offset + 2 <= count; offset += 2)
            {
                short sample = BitConverter.ToInt16(buffer, offset);
                short scaled = (short)Math.Clamp(
                    (int)Math.Round(sample * gain),
                    short.MinValue,
                    short.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), scaled);
            }
        }
    }

    private void StopSystemCapture()
    {
        WasapiLoopbackCapture? capture = _systemCapture;
        _systemCapture = null;
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        lock (_systemLock)
        {
            if (_systemWriter is not null)
                FinalizeSystemWriterCore(Math.Min(Environment.TickCount64, _systemSegmentStart + SegmentMilliseconds), true);
        }

        IsRecordingSystem = false;
    }

    private void StopMicrophoneCapture()
    {
        WasapiCapture? capture = _microphoneCapture;
        _microphoneCapture = null;
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        lock (_microphoneLock)
        {
            if (_microphoneWriter is not null)
                FinalizeMicrophoneWriterCore(Math.Min(Environment.TickCount64, _microphoneSegmentStart + SegmentMilliseconds), true);
        }

        IsRecordingMic = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_systemLock)
        {
            _continuousSystemWriter?.Dispose();
            _continuousSystemWriter = null;
        }
        lock (_microphoneLock)
        {
            _continuousMicrophoneWriter?.Dispose();
            _continuousMicrophoneWriter = null;
        }
        StopSystemCapture();
        StopMicrophoneCapture();
        _systemSegments.Dispose();
        _microphoneSegments.Dispose();
        TryDeleteDirectory(_sessionDirectory);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
