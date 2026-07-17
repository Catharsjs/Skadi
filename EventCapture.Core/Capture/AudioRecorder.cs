using System.Diagnostics;
using EventCapture.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

public sealed class AudioRecorder : IDisposable
{
    private enum AudioInputSource { System, Microphone }

    private const int SegmentMilliseconds = 2_000;
    private const int GapThresholdMilliseconds = 100;
    private const int NativeMixSampleRate = 48_000;
    private const int NativeMixChannels = 2;
    private const int NativeMixChunkMilliseconds = 50;
    private const int NativeMixPumpLatencyMilliseconds = 500;
    private const float NativeMixLimiterThreshold = 0.95f;
    private const int NativeMixLimiterReleaseMilliseconds = 250;

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
    private long _continuousStartTimestamp;
    private IContinuousAudioSink? _continuousAudioSink;
    private readonly object _nativeMixLock = new();
    private Queue<float>? _nativeSystemMix;
    private Queue<float>? _nativeMicrophoneMix;
    private bool _nativeMixSystemEnabled;
    private bool _nativeMixMicrophoneEnabled;
    private long _nativeMixNextTimestamp;
    private long _nativeMixSystemPackets;
    private long _nativeMixMicrophonePackets;
    private long _nativeMixSystemFrames;
    private long _nativeMixMicrophoneFrames;
    private long _nativeMixSystemInputFrames;
    private long _nativeMixMicrophoneInputFrames;
    private long _nativeMixChunks;
    private long _nativeMixWrittenFrames;
    private long _nativeMixLastLogTimestamp;
    private double _nativeMixLimiterGain;
    private double _nativeMixMinimumLimiterGain;
    private float _nativeMixPeakBeforeLimiter;
    private long _nativeMixLimitedFrames;
    private long _nativeSystemResampleRemainder;
    private long _nativeMicrophoneResampleRemainder;
    private long _nativeSystemTimelineFrames;
    private long _nativeMicrophoneTimelineFrames;
    private bool _nativeSystemTimelineInitialized;
    private bool _nativeMicrophoneTimelineInitialized;
    private System.Threading.Timer? _nativeMixPumpTimer;
    private bool _nativeMixClockPumped;
    private Exception? _nativeMixFailure;
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
    public static WaveFormat NativeContinuousMixFormat { get; } = new(NativeMixSampleRate, 16, NativeMixChannels);

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

    // Запуск захоплення системного звуку та мікрофона ...
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
    // ...Запуск захоплення системного звуку та мікрофона

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
            QueueNativeContinuousAudio(
                AudioInputSource.System,
                format,
                buffer,
                count,
                packetStart);
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
            QueueNativeContinuousAudio(
                AudioInputSource.Microphone,
                format,
                buffer,
                count,
                packetStart);
            _microphoneWrittenBytes += count;
            _microphoneLastPacketEnd = packetEnd;
        }
    }

    // Збереження replay з відео та аудіо ...
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
        string outputPath = ReplayAudioExporter.IsInsideFolder(videoPath, outputFolder)
            ? videoPath
            : OutputFileName.Create(outputFolder, "Replay", ".mp4");
        string mergeOutputPath = Path.Combine(
            outputFolder,
            $".replay-merge-{Guid.NewGuid():N}.tmp.mp4");

        try
        {
            await ReplayAudioExporter.MuxWithVideoAsync(videoPath, mixedAudio, mergeOutputPath);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(mergeOutputPath, outputPath, overwrite: true);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        finally
        {
            TryDelete(mixedAudio);
            TryDelete(mergeOutputPath);
        }
    }
    // ...Збереження replay з відео та аудіо

    // Збереження аудіо з replay buffer у MP3 ...
    public async Task<string?> SaveAudioLastSecondsAsMp3Async(string outputFolder, int seconds)
    {
        long windowEnd = Environment.TickCount64;
        long windowStart = Math.Max(_sharedStartTimestamp, windowEnd - Math.Max(1, seconds) * 1000L);
        string? mixedAudio = await CreateAudioSnapshotAsync(windowStart, windowEnd);
        if (mixedAudio is null) return null;

        string outputPath = OutputFileName.Create(outputFolder, "Audio", ".mp3");

        try
        {
            await ReplayAudioExporter.EncodeMp3Async(mixedAudio, outputPath);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        finally
        {
            TryDelete(mixedAudio);
        }
    }
    // ...Збереження аудіо з replay buffer у MP3


    // Запуск безперервного передавання змішаного аудіо ...
    public void StartContinuousNativeStreaming(IContinuousAudioSink sink)
    {
        ThrowIfDisposed();
        if (_continuousRecording)
            throw new InvalidOperationException("Continuous audio recording is already active.");
        if (!IsRecordingSystem && !IsRecordingMic)
            throw new InvalidOperationException("No active audio source is available.");

        _continuousStartTimestamp = Environment.TickCount64;
        lock (_nativeMixLock)
        {
            _continuousAudioSink = sink;
            _nativeSystemMix = new Queue<float>();
            _nativeMicrophoneMix = new Queue<float>();
            _nativeMixSystemEnabled = IsRecordingSystem;
            _nativeMixMicrophoneEnabled = IsRecordingMic;
            _nativeMixNextTimestamp = _continuousStartTimestamp;
            _nativeMixSystemPackets = 0;
            _nativeMixMicrophonePackets = 0;
            _nativeMixSystemFrames = 0;
            _nativeMixMicrophoneFrames = 0;
            _nativeMixSystemInputFrames = 0;
            _nativeMixMicrophoneInputFrames = 0;
            _nativeMixChunks = 0;
            _nativeMixWrittenFrames = 0;
            _nativeMixLastLogTimestamp = 0;
            _nativeMixLimiterGain = 1.0;
            _nativeMixMinimumLimiterGain = 1.0;
            _nativeMixPeakBeforeLimiter = 0;
            _nativeMixLimitedFrames = 0;
            _nativeSystemResampleRemainder = 0;
            _nativeMicrophoneResampleRemainder = 0;
            _nativeSystemTimelineFrames = 0;
            _nativeMicrophoneTimelineFrames = 0;
            _nativeSystemTimelineInitialized = false;
            _nativeMicrophoneTimelineInitialized = false;
            _nativeMixClockPumped = true;
            _nativeMixFailure = null;
        }
        _continuousRecording = true;
        if (_nativeMixClockPumped)
        {
            _nativeMixPumpTimer = new System.Threading.Timer(
                _ => PumpNativeMixClock(),
                null,
                NativeMixChunkMilliseconds,
                NativeMixChunkMilliseconds);
        }
        AppLogger.Info($"Continuous native audio mix started | SystemEnabled={IsRecordingSystem} | MicrophoneEnabled={IsRecordingMic} | ClockPump={_nativeMixClockPumped} | JitterBufferMs={NativeMixPumpLatencyMilliseconds} | SystemFormat={_systemFormat} | MicrophoneFormat={_microphoneFormat} | MixFormat={NativeContinuousMixFormat}");
    }
    // ...Запуск безперервного передавання змішаного аудіо

    // Зупинка безперервного передавання змішаного аудіо ...
    public (long StartTimestamp, long EndTimestamp) StopContinuousNativeStreaming()
    {
        if (!_continuousRecording)
            throw new InvalidOperationException("Continuous audio recording is not active.");

        System.Threading.Timer? pumpTimer = Interlocked.Exchange(ref _nativeMixPumpTimer, null);
        try { pumpTimer?.Dispose(); } catch { }
        long endTimestamp = Environment.TickCount64;
        Exception? mixFailure;
        lock (_nativeMixLock)
        {
            try
            {
                if (_nativeMixFailure is null)
                {
                    if (_nativeMixClockPumped)
                        PumpNativeMixedAudioToTimestampLocked(endTimestamp, includePartialChunk: true);
                    else
                        FlushNativeMixedAudioLocked(force: true);
                }
            }
            catch (Exception ex)
            {
                _nativeMixFailure ??= ex;
            }
            mixFailure = _nativeMixFailure;
            AppLogger.Info($"Continuous native audio mix stopped | SystemPackets={_nativeMixSystemPackets} | MicrophonePackets={_nativeMixMicrophonePackets} | SystemInputFrames={_nativeMixSystemInputFrames} | MicrophoneInputFrames={_nativeMixMicrophoneInputFrames} | SystemFrames={_nativeMixSystemFrames} | MicrophoneFrames={_nativeMixMicrophoneFrames} | MixedChunks={_nativeMixChunks} | MixedFrames={_nativeMixWrittenFrames} | PeakBeforeLimiter={_nativeMixPeakBeforeLimiter:0.####} | MinimumLimiterGain={_nativeMixMinimumLimiterGain:0.####} | LimitedFrames={_nativeMixLimitedFrames} | RemainingSystemSamples={_nativeSystemMix?.Count ?? 0} | RemainingMicrophoneSamples={_nativeMicrophoneMix?.Count ?? 0} | NextTimestamp={_nativeMixNextTimestamp}");
            _continuousAudioSink = null;
            _nativeSystemMix = null;
            _nativeMicrophoneMix = null;
            _nativeMixSystemEnabled = false;
            _nativeMixMicrophoneEnabled = false;
            _nativeMixClockPumped = false;
            _nativeMixFailure = null;
        }
        _continuousRecording = false;
        if (mixFailure is not null)
            throw new InvalidOperationException("Continuous audio streaming failed.", mixFailure);
        return (_continuousStartTimestamp, endTimestamp);
    }
    // ...Зупинка безперервного передавання змішаного аудіо

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
        await ReplayAudioExporter.MixSegmentsAsync(segments, windowStart, windowEnd, outputPath);
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

    private void QueueNativeContinuousAudio(
        AudioInputSource source,
        WaveFormat format,
        byte[] buffer,
        int count,
        long packetStartTimestamp)
    {
        if (_continuousAudioSink is null || count <= 0) return;

        try
        {
            ref long resampleRemainder = ref (
                source == AudioInputSource.Microphone
                    ? ref _nativeMicrophoneResampleRemainder
                    : ref _nativeSystemResampleRemainder);
            float[] stereoSamples = ConvertToNativeMixSamples(
                format,
                buffer,
                count,
                ref resampleRemainder);
            if (stereoSamples.Length == 0)
            {
                AppLogger.Info($"Continuous native audio packet skipped | Source={source} | Format={format} | Bytes={count}");
                return;
            }

            lock (_nativeMixLock)
            {
                Queue<float>? queue = source == AudioInputSource.Microphone
                    ? _nativeMicrophoneMix
                    : _nativeSystemMix;
                if (queue is null) return;

                ref long timelineFrames = ref (
                    source == AudioInputSource.Microphone
                        ? ref _nativeMicrophoneTimelineFrames
                        : ref _nativeSystemTimelineFrames);
                ref bool timelineInitialized = ref (
                    source == AudioInputSource.Microphone
                        ? ref _nativeMicrophoneTimelineInitialized
                        : ref _nativeSystemTimelineInitialized);

                int packetFrames = stereoSamples.Length / NativeMixChannels;
                if (source == AudioInputSource.Microphone)
                    _nativeMixMicrophoneInputFrames += packetFrames;
                else
                    _nativeMixSystemInputFrames += packetFrames;
                if (!timelineInitialized)
                {
                    timelineFrames = Math.Max(
                        0,
                        packetStartTimestamp - _continuousStartTimestamp) *
                        NativeMixSampleRate / 1000L;
                    timelineInitialized = true;
                    AppLogger.Info(
                        $"Continuous native audio clock anchored | Source={source} | " +
                        $"StartFrame={timelineFrames} | PacketFrames={packetFrames} | " +
                        $"PacketStart={packetStartTimestamp} | ContinuousStart={_continuousStartTimestamp}");
                }

                long desiredStartFrame = timelineFrames;
                if (_nativeMixClockPumped)
                {
                    long timestampStartFrame = Math.Max(
                        0,
                        packetStartTimestamp - _continuousStartTimestamp) *
                        NativeMixSampleRate / 1000L;
                    long gapThresholdFrames =
                        GapThresholdMilliseconds * NativeMixSampleRate / 1000L;
                    if (timestampStartFrame - timelineFrames > gapThresholdFrames)
                        desiredStartFrame = timestampStartFrame;
                }
                timelineFrames = desiredStartFrame + packetFrames;
                long queueEndFrame = _nativeMixWrittenFrames +
                    queue.Count / NativeMixChannels;
                long missingFrames = desiredStartFrame - queueEndFrame;
                if (missingFrames > 0)
                {
                    for (long frame = 0; frame < missingFrames; frame++)
                    {
                        queue.Enqueue(0);
                        queue.Enqueue(0);
                    }
                    queueEndFrame += missingFrames;
                }

                int overlapFrames = checked((int)Math.Min(
                    Math.Max(0, queueEndFrame - desiredStartFrame),
                    packetFrames));
                int firstSample = overlapFrames * NativeMixChannels;

                for (int sample = firstSample; sample < stereoSamples.Length; sample++)
                    queue.Enqueue(stereoSamples[sample]);

                int frames = packetFrames - overlapFrames;
                if (source == AudioInputSource.Microphone)
                {
                    _nativeMixMicrophonePackets++;
                    _nativeMixMicrophoneFrames += frames;
                }
                else
                {
                    _nativeMixSystemPackets++;
                    _nativeMixSystemFrames += frames;
                }

                LogNativeMixStatusLocked($"packet-{source}", force: false);
                FlushNativeMixedAudioLocked(force: false);
            }
        }
        catch (Exception ex)
        {
            if (_nativeMixClockPumped)
            {
                lock (_nativeMixLock)
                {
                    _nativeMixFailure ??= ex;
                }
            }
            AppLogger.Error(nameof(AudioRecorder), $"Continuous native audio mix failed: {ex}");
        }
    }

    private void PumpNativeMixClock()
    {
        try
        {
            lock (_nativeMixLock)
            {
                if (!_continuousRecording ||
                    !_nativeMixClockPumped ||
                    _continuousAudioSink is null ||
                    _nativeMixFailure is not null)
                {
                    return;
                }

                long targetTimestamp = Math.Max(
                    _continuousStartTimestamp,
                    Environment.TickCount64 - NativeMixPumpLatencyMilliseconds);
                PumpNativeMixedAudioToTimestampLocked(
                    targetTimestamp,
                    includePartialChunk: false);
            }
        }
        catch (Exception ex)
        {
            lock (_nativeMixLock)
            {
                _nativeMixFailure ??= ex;
            }
            AppLogger.Error(nameof(AudioRecorder), $"Continuous audio clock pump failed: {ex}");
        }
    }

    private void PumpNativeMixedAudioToTimestampLocked(
        long targetTimestamp,
        bool includePartialChunk)
    {
        Queue<float>? system = _nativeSystemMix;
        Queue<float>? microphone = _nativeMicrophoneMix;
        if (_continuousAudioSink is null || system is null || microphone is null)
            return;

        long targetFrames = Math.Max(
            0,
            targetTimestamp - _continuousStartTimestamp) *
            NativeMixSampleRate / 1000L;
        int chunkFrames = NativeMixSampleRate * NativeMixChunkMilliseconds / 1000;

        while (_nativeMixWrittenFrames < targetFrames)
        {
            long remainingFrames = targetFrames - _nativeMixWrittenFrames;
            int frames = (int)Math.Min(chunkFrames, remainingFrames);
            if (!includePartialChunk && frames < chunkFrames)
                break;

            int requiredSamples = frames * NativeMixChannels;
            if (_nativeMixSystemEnabled)
                PadNativeMixQueue(system, requiredSamples);
            if (_nativeMixMicrophoneEnabled)
                PadNativeMixQueue(microphone, requiredSamples);

            FlushNativeMixedAudioLocked(force: frames < chunkFrames);
            if (frames < chunkFrames)
                break;
        }
    }

    private static void PadNativeMixQueue(Queue<float> queue, int requiredSamples)
    {
        while (queue.Count < requiredSamples)
            queue.Enqueue(0);
    }

    private void FlushNativeMixedAudioLocked(bool force)
    {
        IContinuousAudioSink? sink = _continuousAudioSink;
        Queue<float>? system = _nativeSystemMix;
        Queue<float>? microphone = _nativeMicrophoneMix;
        if (sink is null || system is null || microphone is null) return;

        int chunkSamples = NativeMixSampleRate * NativeMixChannels * NativeMixChunkMilliseconds / 1000;
        while (true)
        {
            int systemAvailable = _nativeMixSystemEnabled ? system.Count : int.MaxValue;
            int microphoneAvailable = _nativeMixMicrophoneEnabled ? microphone.Count : int.MaxValue;
            int synchronizedAvailable = Math.Min(systemAvailable, microphoneAvailable);
            if (!_nativeMixSystemEnabled && !_nativeMixMicrophoneEnabled)
                synchronizedAvailable = 0;

            int maxAvailable = Math.Max(
                _nativeMixSystemEnabled ? system.Count : 0,
                _nativeMixMicrophoneEnabled ? microphone.Count : 0);
            int samplesToWrite = force
                ? Math.Min(chunkSamples, maxAvailable - (maxAvailable % NativeMixChannels))
                : synchronizedAvailable >= chunkSamples ? chunkSamples : 0;

            if (samplesToWrite <= 0)
                break;

            int frames = samplesToWrite / NativeMixChannels;
            byte[] pcm = new byte[frames * NativeMixChannels * sizeof(short)];
            double releaseCoefficient = 1.0 - Math.Exp(
                -1.0 /
                (NativeMixSampleRate * NativeMixLimiterReleaseMilliseconds / 1000.0));
            for (int frame = 0; frame < frames; frame++)
            {
                float left = DequeueMixedSample(system, microphone);
                float right = DequeueMixedSample(system, microphone);
                float peak = Math.Max(Math.Abs(left), Math.Abs(right));
                _nativeMixPeakBeforeLimiter = Math.Max(_nativeMixPeakBeforeLimiter, peak);

                double requiredGain = peak > NativeMixLimiterThreshold
                    ? NativeMixLimiterThreshold / peak
                    : 1.0;
                if (requiredGain < _nativeMixLimiterGain)
                    _nativeMixLimiterGain = requiredGain;
                else
                    _nativeMixLimiterGain +=
                        (1.0 - _nativeMixLimiterGain) * releaseCoefficient;

                _nativeMixMinimumLimiterGain = Math.Min(
                    _nativeMixMinimumLimiterGain,
                    _nativeMixLimiterGain);
                if (_nativeMixLimiterGain < 0.9999)
                    _nativeMixLimitedFrames++;

                WritePcm16Sample(pcm, frame * NativeMixChannels, left, _nativeMixLimiterGain);
                WritePcm16Sample(pcm, frame * NativeMixChannels + 1, right, _nativeMixLimiterGain);
            }

            long durationMs = Math.Max(1, frames * 1000L / NativeMixSampleRate);
            long chunkTimestamp = _continuousStartTimestamp +
                _nativeMixWrittenFrames * 1000L / NativeMixSampleRate;

            sink.WriteContinuousAudio(
                NativeContinuousMixFormat,
                pcm,
                pcm.Length,
                chunkTimestamp,
                durationMs);
            _nativeMixNextTimestamp = chunkTimestamp + durationMs;
            _nativeMixChunks++;
            _nativeMixWrittenFrames += frames;
            LogNativeMixStatusLocked(force ? "flush-force" : "flush", force);
        }
    }

    private void LogNativeMixStatusLocked(string reason, bool force)
    {
        long now = Environment.TickCount64;
        if (_nativeMixChunks > 3 && now - _nativeMixLastLogTimestamp < 2_000) return;
        _nativeMixLastLogTimestamp = now;
        int systemSamples = _nativeSystemMix?.Count ?? 0;
        int microphoneSamples = _nativeMicrophoneMix?.Count ?? 0;
        long systemQueueMs = systemSamples / NativeMixChannels * 1000L / NativeMixSampleRate;
        long microphoneQueueMs = microphoneSamples / NativeMixChannels * 1000L / NativeMixSampleRate;
        AppLogger.Info($"Continuous native audio mix status | Reason={reason} | Force={force} | SystemEnabled={_nativeMixSystemEnabled} | MicrophoneEnabled={_nativeMixMicrophoneEnabled} | SystemPackets={_nativeMixSystemPackets} | MicrophonePackets={_nativeMixMicrophonePackets} | SystemQueueSamples={systemSamples} | MicrophoneQueueSamples={microphoneSamples} | SystemQueueMs={systemQueueMs} | MicrophoneQueueMs={microphoneQueueMs} | MixedChunks={_nativeMixChunks} | MixedFrames={_nativeMixWrittenFrames} | NextTimestamp={_nativeMixNextTimestamp}");
    }

    private static float[] ConvertToNativeMixSamples(
        WaveFormat format,
        byte[] buffer,
        int count,
        ref long rateRemainder)
    {
        int blockAlign = Math.Max(1, format.BlockAlign);
        int inputFrames = count / blockAlign;
        if (inputFrames <= 0 || format.SampleRate <= 0 || format.Channels <= 0)
            return [];

        long scaledFrames = checked((long)inputFrames * NativeMixSampleRate + rateRemainder);
        int outputFrames = checked((int)(scaledFrames / format.SampleRate));
        rateRemainder = scaledFrames % format.SampleRate;
        if (outputFrames <= 0)
            return [];
        float[] output = new float[outputFrames * NativeMixChannels];
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double sourcePosition = outputFrames == 1
                ? 0
                : frame * (inputFrames - 1.0) / (outputFrames - 1.0);
            int sourceFrame = Math.Min(inputFrames - 1, (int)sourcePosition);
            int nextSourceFrame = Math.Min(inputFrames - 1, sourceFrame + 1);
            float fraction = (float)(sourcePosition - sourceFrame);
            float left = Lerp(
                ReadSampleAsFloat(format, buffer, count, sourceFrame, 0),
                ReadSampleAsFloat(format, buffer, count, nextSourceFrame, 0),
                fraction);
            float right = format.Channels == 1
                ? left
                : Lerp(
                    ReadSampleAsFloat(format, buffer, count, sourceFrame, 1),
                    ReadSampleAsFloat(format, buffer, count, nextSourceFrame, 1),
                    fraction);
            output[frame * NativeMixChannels] = left;
            output[frame * NativeMixChannels + 1] = right;
        }

        return output;
    }

    private static float DequeueMixedSample(
        Queue<float> system,
        Queue<float> microphone)
    {
        float mixed = 0;
        if (system.Count > 0) mixed += system.Dequeue();
        if (microphone.Count > 0) mixed += microphone.Dequeue();
        return mixed;
    }

    private static void WritePcm16Sample(
        byte[] destination,
        int sampleIndex,
        float sample,
        double gain)
    {
        short pcmSample = (short)Math.Clamp(
            (int)Math.Round(Math.Clamp(sample * gain, -1.0, 1.0) * short.MaxValue),
            short.MinValue,
            short.MaxValue);
        BitConverter.TryWriteBytes(
            destination.AsSpan(sampleIndex * sizeof(short), sizeof(short)),
            pcmSample);
    }

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;

    private static float ReadSampleAsFloat(WaveFormat format, byte[] buffer, int count, int frame, int channel)
    {
        int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        int offset = frame * Math.Max(1, format.BlockAlign) + Math.Min(channel, format.Channels - 1) * bytesPerSample;
        if (offset < 0 || offset + bytesPerSample > count)
            return 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.0f, 1.0f);

        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(buffer, offset) / 32768f;

        if (format.BitsPerSample == 24)
        {
            int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            return Math.Clamp(sample / 8388608f, -1.0f, 1.0f);
        }

        if (format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToInt32(buffer, offset) / 2147483648f, -1.0f, 1.0f);

        return 0;
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

    // Зупинка аудіозахоплення та звільнення ресурсів ...
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        System.Threading.Timer? pumpTimer = Interlocked.Exchange(ref _nativeMixPumpTimer, null);
        try { pumpTimer?.Dispose(); } catch { }
        lock (_nativeMixLock)
        {
            _continuousAudioSink = null;
            _nativeSystemMix = null;
            _nativeMicrophoneMix = null;
            _nativeMixClockPumped = false;
            _nativeMixFailure = null;
            _continuousRecording = false;
        }
        StopSystemCapture();
        StopMicrophoneCapture();
        _systemSegments.Dispose();
        _microphoneSegments.Dispose();
        TryDeleteDirectory(_sessionDirectory);
    }
    // ...Зупинка аудіозахоплення та звільнення ресурсів

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
