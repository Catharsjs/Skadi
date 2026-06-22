using System.Diagnostics;
using System.Globalization;
using EventCapture.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
namespace EventCapture.Core.Capture;

// Паралельний запис системного звуку та мікрофона через (WASAPI)
// Кожен audio device restart створює окремий сегмент із власними timestamp.
public class AudioRecorder : IDisposable
{
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _loopbackWriter;
    private WaveFileWriter? _micWriter;
    private MMDeviceEnumerator? _deviceEnumerator;
    private AudioDeviceChangeCallback? _deviceChangeCallback;
    private readonly object _loopbackLock = new();
    private readonly object _micLock = new();
    private readonly object _segmentsLock = new();
    private long _sharedStartTimestamp;
    private long _audioActualStartTimestamp;
    private string _loopbackTempPath = string.Empty;
    private string _micTempPath = string.Empty;
    private readonly List<AudioSegment> _loopbackSegments = new();
    private readonly List<AudioSegment> _micSegments = new();
    private double _systemGain = 1.0;
    private double _microphoneGain = 1.0;

    public readonly List<string> LoopbackTempPaths = new();
    public event Action<string>? DefaultDeviceChanged;
    public bool IsRecordingSystem { get; private set; }
    public bool IsRecordingMic { get; private set; }
    public bool UseDefaultSystemDevice { get; set; } = true;
    public bool UseDefaultMicDevice { get; set; } = true;

    public void SetSystemVolume(double percent) =>
        Volatile.Write(ref _systemGain, Math.Clamp(percent, 0, 100) / 100.0);

    public void SetMicrophoneVolume(double percent) =>
        Volatile.Write(ref _microphoneGain, Math.Clamp(percent, 0, 100) / 100.0);

    private sealed class AudioSegment
    {
        public string Path { get; set; } = string.Empty;
        public long StartTimestamp { get; set; }
        public long ActualStartTimestamp { get; set; }
        public long FirstFrameTimestamp { get; set; }
        public long FirstBufferDurationMs { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public long TimelineStartTimestamp { get; set; }
        public long LastPacketEndTimestamp { get; set; }
        public long WrittenBytes { get; set; }
    }

    // Аудіо пристрої (...
    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var devices = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add((device.ID, device.FriendlyName));
        }
        return devices;
    }

    public static List<(string Id, string Name)> GetInputDevices()
    {
        var devices = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            devices.Add((device.ID, device.FriendlyName));
        }
        return devices;
    }
    // ...) Аудіо пристрої

    // Цикл запису (...
    public void StartRecording(
        bool recordSystem,
        string? systemDeviceId,
        bool recordMic,
        string? micDeviceId,
        long sharedStartTimestamp = 0)
    {
        StopDeviceChangeMonitoring();
        StartDeviceChangeMonitoring();
        CleanupOldTempFiles();

        lock (_segmentsLock)
        {
            LoopbackTempPaths.Clear();
            _loopbackSegments.Clear();
            _micSegments.Clear();
        }

        _sharedStartTimestamp = sharedStartTimestamp > 0
            ? sharedStartTimestamp
            : Environment.TickCount64;

        _audioActualStartTimestamp = 0;

        if (recordSystem)
            StartSystemCapture(systemDeviceId);

        if (recordMic)
            StartMicCapture(micDeviceId);
    }

    public void RestartSystemCapture(string? deviceId)
    {
        StopSystemCaptureOnly();
        IsRecordingSystem = false;
        UseDefaultSystemDevice = deviceId == null;
        StartSystemCapture(deviceId);
    }

    public void RestartMicCapture(string? deviceId)
    {
        StopMicCaptureOnly();
        IsRecordingMic = false;
        UseDefaultMicDevice = deviceId == null;
        StartMicCapture(deviceId);
    }

    private void StopCapture()
    {
        StopSystemCaptureOnly();
        StopMicCaptureOnly();
        IsRecordingSystem = false;
        IsRecordingMic = false;
        _audioActualStartTimestamp = 0;
        StopDeviceChangeMonitoring();
    }

    public void Dispose()
    {
        StopCapture();
    }
    // ...) Цикл запису

    // Захоплення системного аудіо (...
    private void StartSystemCapture(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

            string tempPath = Path.Combine(Path.GetTempPath(), $"eventcapture_audio_system_{Guid.NewGuid()}.wav");
            var capture = new WasapiLoopbackCapture(device);
            var writer = new WaveFileWriter(tempPath, capture.WaveFormat);
            var segment = new AudioSegment
            {
                Path = tempPath,
                StartTimestamp = Environment.TickCount64,
                DeviceName = device.FriendlyName
            };

            _loopbackCapture = capture;
            _loopbackWriter = writer;
            _loopbackTempPath = tempPath;

            lock (_segmentsLock)
            {
                LoopbackTempPaths.Add(tempPath);
                _loopbackSegments.Add(segment);
            }

            AppLogger.Info($"StartSystemCapture | Device={device.FriendlyName} | Path={tempPath}");
            int frameCount = 0;

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                ApplyGain(
                    e.Buffer,
                    e.BytesRecorded,
                    capture.WaveFormat,
                    Volatile.Read(ref _systemGain));

                lock (_loopbackLock)
                {
                    if (_loopbackWriter is null)
                    {
                        return;
                    }

                    WriteTimelineAudioPacket(
                        _loopbackWriter,
                        segment,
                        capture.WaveFormat,
                        e.Buffer,
                        e.BytesRecorded,
                        "System");
                }

                frameCount++;

                if (frameCount % 100 == 0)
                {
                    AppLogger.Debug(
                        $"System audio frame | Device={segment.DeviceName} | " +
                        $"Frame={frameCount} | Bytes={e.BytesRecorded}");
                }
            };

            capture.StartRecording();

            long actualStartTimestamp = Environment.TickCount64;
            _audioActualStartTimestamp = actualStartTimestamp;
            segment.ActualStartTimestamp = actualStartTimestamp;

            IsRecordingSystem = true;

            AppLogger.Info($"System audio started | Device={device.FriendlyName} | " +
                           $"Delay={actualStartTimestamp - _sharedStartTimestamp}ms");
        }
        catch (Exception ex)
        {
            IsRecordingSystem = false;
            AppLogger.Error(nameof(AudioRecorder), $"StartSystemCapture failed: {ex}");
        }
    }

    private void StopSystemCaptureOnly()
    {
        if (_loopbackCapture != null)
        {
            try
            {
                _loopbackCapture.StopRecording();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Stop system capture warning: {ex.Message}");
            }
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }

        AudioSegment? activeSegment;

        lock (_segmentsLock)
        {
            activeSegment =
                _loopbackSegments.LastOrDefault();
        }

        lock (_loopbackLock)
        {
            if (_loopbackWriter is not null)
            {
                if (activeSegment is not null)
                {
                    AppendTrailingSilence(
                        _loopbackWriter,
                        activeSegment);
                }

                _loopbackWriter.Flush();
                _loopbackWriter.Dispose();
                _loopbackWriter = null;
            }
        }
    }
    // ...) Захоплення системного аудіо

    // Захоплення мікрофона (...
    private void StartMicCapture(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Multimedia);

            AppLogger.Info(
                $"StartMicCapture resolved | RequestedId={deviceId ?? "null"} | " +
                $"ActualId={device.ID} | Name={device.FriendlyName}");

            string tempPath = Path.Combine(Path.GetTempPath(), $"eventcapture_audio_mic_{Guid.NewGuid()}.wav");
            var capture = new WasapiCapture(device);
            var writer = new WaveFileWriter(tempPath, capture.WaveFormat);
            var segment = new AudioSegment
            {
                Path = tempPath,
                StartTimestamp = Environment.TickCount64,
                DeviceName = $"Mic: {device.FriendlyName}"
            };

            _micCapture = capture;
            _micWriter = writer;
            _micTempPath = tempPath;

            lock (_segmentsLock)
            {
                _micSegments.Add(segment);
            }

            AppLogger.Info($"StartMicCapture | Device={device.FriendlyName} | Path={tempPath}");

            int frameCount = 0;

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                ApplyGain(
                    e.Buffer,
                    e.BytesRecorded,
                    capture.WaveFormat,
                    Volatile.Read(ref _microphoneGain));

                lock (_micLock)
                {
                    if (_micWriter is null)
                    {
                        return;
                    }

                    WriteTimelineAudioPacket(
                        _micWriter,
                        segment,
                        capture.WaveFormat,
                        e.Buffer,
                        e.BytesRecorded,
                        "Microphone");
                }

                frameCount++;
            };

            capture.StartRecording();

            long actualStartTimestamp = Environment.TickCount64;
            segment.ActualStartTimestamp = actualStartTimestamp;

            IsRecordingMic = true;

            AppLogger.Info(
                $"Microphone started | Device={device.FriendlyName} | " +
                $"Delay={actualStartTimestamp - _sharedStartTimestamp}ms");
        }
        catch (Exception ex)
        {
            IsRecordingMic = false;
            AppLogger.Error( nameof(AudioRecorder), $"StartMicCapture failed: {ex}");
        }
    }

    private void StopMicCaptureOnly()
    {
        if (_micCapture != null)
        {
            try
            {
                _micCapture.StopRecording();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Stop microphone capture warning: {ex.Message}");
            }
            _micCapture.Dispose();
            _micCapture = null;
        }

        AudioSegment? activeSegment;

        lock (_segmentsLock)
        {
            activeSegment =
                _micSegments.LastOrDefault();
        }

        lock (_micLock)
        {
            if (_micWriter is not null)
            {
                if (activeSegment is not null)
                {
                    AppendTrailingSilence(
                        _micWriter,
                        activeSegment);
                }

                _micWriter.Flush();
                _micWriter.Dispose();
                _micWriter = null;
            }
        }
    }
    // ...) Захоплення мікрофона

    // Синхронізація сегментів (...
    private void RegisterFirstAudioFrame(
        AudioSegment segment,
        WaveFormat format,
        int bytesRecorded,
        string sourceName)
    {
        long firstFrameTimestamp = Environment.TickCount64;
        long firstBufferDurationMs = CalculateBufferDurationMs(
            format,
            bytesRecorded);

        segment.FirstFrameTimestamp = firstFrameTimestamp;
        segment.FirstBufferDurationMs = firstBufferDurationMs;

        AppLogger.Info(
            $"{sourceName} first frame | Device={segment.DeviceName} | " +
            $"FirstFrame={firstFrameTimestamp} | BufferMs={firstBufferDurationMs} | " +
            $"DelayFromVideo={firstFrameTimestamp - _sharedStartTimestamp}ms");
    }

    private static long CalculateBufferDurationMs(WaveFormat format, int bytesRecorded)
    {
        int bytesPerSample =
            Math.Max(1, format.BitsPerSample / 8);

        int bytesPerSecond =
            format.SampleRate *
            Math.Max(1, format.Channels) *
            bytesPerSample;

        if (bytesPerSecond <= 0)
            return 0;

        return (long)(
            bytesRecorded *
            1000.0 /
            bytesPerSecond);
    }

    private void WriteTimelineAudioPacket(
    WaveFileWriter writer,
    AudioSegment segment,
    WaveFormat format,
    byte[] buffer,
    int count,
    string sourceName)
    {
        long packetEndTimestamp =
            Environment.TickCount64;

        long packetDurationMs =
            CalculateBufferDurationMs(
                format,
                count);

        long packetStartTimestamp =
            packetEndTimestamp -
            packetDurationMs;

        if (segment.FirstFrameTimestamp == 0)
        {
            segment.TimelineStartTimestamp =
                packetStartTimestamp;

            RegisterFirstAudioFrame(
                segment,
                format,
                count,
                sourceName);
        }
        else
        {
            long expectedStartMs =
                packetStartTimestamp -
                segment.TimelineStartTimestamp;

            long writtenDurationMs =
                CalculateWrittenDurationMs(
                    segment,
                    format);

            long missingDurationMs =
                expectedStartMs -
                writtenDurationMs;

            if (missingDurationMs >= 100)
            {
                segment.WrittenBytes +=
                    WriteSilence(
                        writer,
                        format,
                        missingDurationMs);
            }
        }

        writer.Write(
            buffer,
            0,
            count);

        segment.WrittenBytes += count;
        segment.LastPacketEndTimestamp =
            packetEndTimestamp;
    }

    private static long CalculateWrittenDurationMs(
        AudioSegment segment,
        WaveFormat format)
    {
        if (format.AverageBytesPerSecond <= 0)
        {
            return 0;
        }

        return segment.WrittenBytes *
               1000 /
               format.AverageBytesPerSecond;
    }

    private static long WriteSilence(
        WaveFileWriter writer,
        WaveFormat format,
        long durationMilliseconds)
    {
        if (durationMilliseconds <= 0 ||
            format.AverageBytesPerSecond <= 0)
        {
            return 0;
        }

        long byteCount =
            durationMilliseconds *
            format.AverageBytesPerSecond /
            1000;

        int blockAlign =
            Math.Max(
                1,
                format.BlockAlign);

        byteCount -=
            byteCount % blockAlign;

        if (byteCount <= 0)
        {
            return 0;
        }

        long writtenBytes = 0;

        var silenceBuffer =
            new byte[64 * 1024];

        while (byteCount > 0)
        {
            int count =
                (int)Math.Min(
                    byteCount,
                    silenceBuffer.Length);

            count -= count % blockAlign;

            if (count <= 0)
            {
                break;
            }

            writer.Write(
                silenceBuffer,
                0,
                count);

            byteCount -= count;
            writtenBytes += count;
        }

        return writtenBytes;
    }

    private static void AppendTrailingSilence(
        WaveFileWriter writer,
        AudioSegment segment)
    {
        if (segment.TimelineStartTimestamp <= 0)
        {
            return;
        }

        WaveFormat format =
            writer.WaveFormat;

        long expectedDurationMs =
            Environment.TickCount64 -
            segment.TimelineStartTimestamp;

        long writtenDurationMs =
            CalculateWrittenDurationMs(
                segment,
                format);

        long missingDurationMs =
            expectedDurationMs -
            writtenDurationMs;

        if (missingDurationMs < 100)
        {
            return;
        }

        segment.WrittenBytes +=
            WriteSilence(
                writer,
                format,
                missingDurationMs);
    }

    private static long CalculateAudioFileStartRelativeToVideoMs(
        long firstFrameTimestamp,
        long firstBufferDurationMs,
        long audioActualStartTimestamp,
        long videoStartTimestamp)
    {
        if (videoStartTimestamp <= 0)
            return 0;

        if (firstFrameTimestamp > 0)
        {
            return firstFrameTimestamp -
                   firstBufferDurationMs -
                   videoStartTimestamp;
        }

        if (audioActualStartTimestamp > 0)
            return audioActualStartTimestamp - videoStartTimestamp;

        return 0;
    }

    private static AudioSegment CloneSegment(AudioSegment segment)
    {
        return new AudioSegment
        {
            Path = segment.Path,
            StartTimestamp = segment.StartTimestamp,
            ActualStartTimestamp = segment.ActualStartTimestamp,
            FirstFrameTimestamp = segment.FirstFrameTimestamp,
            FirstBufferDurationMs = segment.FirstBufferDurationMs,
            DeviceName = segment.DeviceName
        };
    }

    private static bool IsValidSegmentForSave(AudioSegment segment)
    {
        if (!File.Exists(segment.Path))
            return false;

        var length = new FileInfo(segment.Path).Length;

        return length > 4096 &&
               segment.FirstFrameTimestamp > 0 &&
               segment.FirstBufferDurationMs > 0;
    }
    // ...) Синхронізація сегментів

    // Збереження і об'єднання (...
    public async Task<string?> SaveLastSecondsAsync(
        string outputFolder,
        int seconds,
        string videoPath,
        long videoElapsedMs,
        long videoStartTimestamp)
    {
        StopCapture();

        await Task.Delay(300);

        var existingSystemSegments = GetValidSystemSegmentsSnapshot();
        var existingMicSegments = GetValidMicSegmentsSnapshot();

        if (existingSystemSegments.Count == 0 &&
            existingMicSegments.Count == 0)
        {
            return null;
        }

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        double videoDuration = await ProbeDurationSecondsAsync(videoPath, seconds);
        long realVideoDurationMs = (long)Math.Round(videoDuration * 1000.0);
        long videoSegmentStartOffsetMs = Math.Max(0, videoElapsedMs - realVideoDurationMs);
        var trimmedAudio = new List<string>();

        await TrimSegmentsAsync(
            ffmpegPath,
            existingSystemSegments,
            videoStartTimestamp,
            videoDuration,
            realVideoDurationMs,
            videoSegmentStartOffsetMs,
            trimmedAudio);

        await TrimSegmentsAsync(
            ffmpegPath,
            existingMicSegments,
            videoStartTimestamp,
            videoDuration,
            realVideoDurationMs,
            videoSegmentStartOffsetMs,
            trimmedAudio);

        if (trimmedAudio.Count == 0)
        {
            CleanupAfterSave(existingSystemSegments, existingMicSegments, trimmedAudio);
            return null;
        }

        string outputPath;
        try
        {
            outputPath = await MergeAudioWithVideoAsync(
                ffmpegPath,
                outputFolder,
                videoPath,
                trimmedAudio);
        }
        finally
        {
            CleanupAfterSave(existingSystemSegments, existingMicSegments, trimmedAudio);
        }

        if (!File.Exists(outputPath) ||
            new FileInfo(outputPath).Length == 0)
        {
            return null;
        }

        return outputPath;
    }

    public async Task<string?> SaveAudioLastSecondsAsMp3Async(
        string outputFolder,
        int seconds)
    {
        long endTimestamp = Environment.TickCount64;
        long elapsedMs = Math.Max(
            1,
            endTimestamp - _sharedStartTimestamp);

        long durationMs = Math.Min(
            elapsedMs,
            Math.Max(1, seconds) * 1000L);

        double durationSeconds =
            durationMs / 1000.0;

        long segmentStartOffsetMs = Math.Max(
            0,
            elapsedMs - durationMs);

        StopCapture();

        await Task.Delay(300);

        var existingSystemSegments =
            GetValidSystemSegmentsSnapshot();

        var existingMicSegments =
            GetValidMicSegmentsSnapshot();

        if (existingSystemSegments.Count == 0 && existingMicSegments.Count == 0)
            return null;

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        var trimmedAudio = new List<string>();

        await TrimSegmentsAsync(
            ffmpegPath, existingSystemSegments, _sharedStartTimestamp,
            durationSeconds, durationMs, segmentStartOffsetMs, trimmedAudio);
        await TrimSegmentsAsync(
            ffmpegPath, existingMicSegments, _sharedStartTimestamp,
            durationSeconds, durationMs, segmentStartOffsetMs, trimmedAudio);

        if (trimmedAudio.Count == 0)
        {
            CleanupAfterSave(existingSystemSegments, existingMicSegments, trimmedAudio);
            return null;
        }

        string outputPath;
        try
        {
            outputPath = await MergeAudioToMp3Async(
                ffmpegPath, outputFolder, trimmedAudio);
        }
        finally
        {
            CleanupAfterSave(existingSystemSegments, existingMicSegments, trimmedAudio);
        }
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
            ? outputPath
            : null;
    }

    private List<AudioSegment> GetValidSystemSegmentsSnapshot()
    {
        lock (_segmentsLock)
        {
            return _loopbackSegments
                .Where(IsValidSegmentForSave)
                .Select(CloneSegment)
                .ToList();
        }
    }

    private List<AudioSegment> GetValidMicSegmentsSnapshot()
    {
        lock (_segmentsLock)
        {
            return _micSegments
                .Where(IsValidSegmentForSave)
                .Select(CloneSegment)
                .ToList();
        }
    }

    private static async Task TrimSegmentsAsync(
        string ffmpegPath,
        IEnumerable<AudioSegment> segments,
        long videoStartTimestamp,
        double videoDuration,
        long realVideoDurationMs,
        long videoSegmentStartOffsetMs,
        List<string> trimmedAudio)
    {
        foreach (var segment in segments)
        {
            string? trimmed = await TrimAudioSegmentAsync(
                    ffmpegPath,
                    segment,
                    videoStartTimestamp,
                    videoDuration,
                    realVideoDurationMs,
                    videoSegmentStartOffsetMs);

            if (!string.IsNullOrEmpty(trimmed))
                trimmedAudio.Add(trimmed);
        }
    }

    private static async Task<string> MergeAudioWithVideoAsync(
        string ffmpegPath,
        string outputFolder,
        string videoPath,
        IReadOnlyList<string> trimmedAudio)
    {
        Directory.CreateDirectory(outputFolder);

        string outputPath = Path.Combine(
            outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" +
            Guid.NewGuid().ToString("N")[..8] +
            "_final.mp4");

        string audioInputArgs =
            string.Join(
                " ",
                trimmedAudio.Select(path => $"-i \"{path}\""));

        string audioFilter = trimmedAudio.Count > 1
            ? $"-filter_complex amix=inputs={trimmedAudio.Count}:duration=longest:dropout_transition=0 -c:a aac"
            : "-c:a aac";

        string arguments =
            $"-y -i \"{videoPath}\" " +
            $"{audioInputArgs} " +
            $"-c:v copy {audioFilter} " +
            $"-shortest \"{outputPath}\"";

        using var process = CreateFFmpegProcess(
            ffmpegPath,
            arguments,
            redirectOutput: false);

        process.Start();

        string error = await process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(30000));

        AppLogger.Info(
            $"Audio/video merge | Output={outputPath} | Exists={File.Exists(outputPath)} | " +
            $"Size={(File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0)}");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            AppLogger.Error(nameof(AudioRecorder), $"FFmpeg merge failed: {error}");
        }
        return outputPath;
    }

    private static async Task<string> MergeAudioToMp3Async(
        string ffmpegPath,
        string outputFolder,
        IReadOnlyList<string> trimmedAudio)
    {
        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" +
            Guid.NewGuid().ToString("N")[..8] + "_audio.mp3");

        string inputs = string.Join(" ", trimmedAudio.Select(path => $"-i \"{path}\""));
        string filter = trimmedAudio.Count > 1
            ? $"-filter_complex \"amix=inputs={trimmedAudio.Count}:duration=longest:dropout_transition=0\" "
            : string.Empty;
        string arguments =
            $"-y {inputs} {filter}-c:a libmp3lame -b:a 192k \"{outputPath}\"";

        using var process = CreateFFmpegProcess(ffmpegPath, arguments, redirectOutput: false);
        process.Start();
        string error = await process.StandardError.ReadToEndAsync();
        await Task.Run(() => process.WaitForExit(30000));

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            AppLogger.Error(nameof(AudioRecorder), $"MP3 export failed: {error}");
        return outputPath;
    }

    private static void ApplyGain(byte[] buffer, int count, WaveFormat format, double gain)
    {
        if (gain >= 0.9999) return;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int offset = 0; offset + 4 <= count; offset += 4)
            {
                float sample = BitConverter.ToSingle(buffer, offset);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), (float)Math.Clamp(sample * gain, -1.0, 1.0));
            }
            return;
        }

        if (format.BitsPerSample == 16)
        {
            for (int offset = 0; offset + 2 <= count; offset += 2)
            {
                short sample = BitConverter.ToInt16(buffer, offset);
                short scaled = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), scaled);
            }
            return;
        }

        if (format.BitsPerSample == 24)
        {
            for (int offset = 0; offset + 3 <= count; offset += 3)
            {
                int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                int scaled = Math.Clamp((int)Math.Round(sample * gain), -8_388_608, 8_388_607);
                buffer[offset] = (byte)scaled;
                buffer[offset + 1] = (byte)(scaled >> 8);
                buffer[offset + 2] = (byte)(scaled >> 16);
            }
            return;
        }

        if (format.BitsPerSample == 32)
        {
            for (int offset = 0; offset + 4 <= count; offset += 4)
            {
                int sample = BitConverter.ToInt32(buffer, offset);
                int scaled = (int)Math.Clamp(Math.Round(sample * gain), int.MinValue, int.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), scaled);
            }
        }
    }

    private void CleanupAfterSave(
        IEnumerable<AudioSegment> systemSegments,
        IEnumerable<AudioSegment> micSegments,
        IEnumerable<string> trimmedAudio)
    {
        foreach (var segment in systemSegments)
            TryDelete(segment.Path);

        foreach (var segment in micSegments)
            TryDelete(segment.Path);

        foreach (var path in trimmedAudio)
            TryDelete(path);

        lock (_segmentsLock)
        {
            LoopbackTempPaths.Clear();
            _loopbackSegments.Clear();
            _micSegments.Clear();
        }
    }
    // ...) Збереження і об'єднання

    // FFmpeg обробка (...
    private static async Task<string?> TrimAudioSegmentAsync(
        string ffmpegPath,
        AudioSegment segment,
        long videoStartTimestamp,
        double videoDuration,
        long realVideoDurationMs,
        long videoSegmentStartOffsetMs)
    {
        if (!IsValidSegmentForSave(segment))
        {
            AppLogger.Debug($"Invalid audio segment skipped | Device={segment.DeviceName} | Path={segment.Path}");
            return null;
        }

        string trimmedPath = Path.Combine(Path.GetTempPath(), $"eventcapture_audio_trim_{Guid.NewGuid()}.wav");

        long audioFileStartRelativeToVideoMs =
            CalculateAudioFileStartRelativeToVideoMs(
                segment.FirstFrameTimestamp,
                segment.FirstBufferDurationMs,
                segment.ActualStartTimestamp > 0
                    ? segment.ActualStartTimestamp
                    : segment.StartTimestamp,
                videoStartTimestamp);

        long ssStartMs =
            videoSegmentStartOffsetMs -
            audioFileStartRelativeToVideoMs;

        long padStartMs = 0;

        if (ssStartMs < 0)
        {
            padStartMs = -ssStartMs;
            ssStartMs = 0;
        }

        string arguments = BuildTrimArguments(
                segment.Path,
                trimmedPath,
                ssStartMs,
                padStartMs,
                videoDuration);

        AppLogger.Debug(
            $"Trim audio | Device={segment.DeviceName} | " +
            $"RealVideoMs={realVideoDurationMs} | VideoOffsetMs={videoSegmentStartOffsetMs} | " +
            $"AudioStartMs={audioFileStartRelativeToVideoMs} | SsMs={ssStartMs} | PadMs={padStartMs}");

        using var process = CreateFFmpegProcess(
            ffmpegPath,
            arguments,
            redirectOutput: false);

        process.Start();

        string error = await process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(15000));

        if (File.Exists(trimmedPath))
        {
            var length = new FileInfo(trimmedPath).Length;

            if (length > 4096)
                return trimmedPath;

            AppLogger.Debug($"Trimmed audio skipped | Size={length} | Path={trimmedPath}");
        }

        TryDelete(trimmedPath);

        if (!string.IsNullOrWhiteSpace(error))
        {
            AppLogger.Debug($"FFmpeg trim output | Device={segment.DeviceName} | {error}");
        }
        return null;
    }

    private static string BuildTrimArguments(
        string inputPath,
        string outputPath,
        long ssStartMs,
        long padStartMs,
        double durationSeconds)
    {
        string ss =
            (ssStartMs / 1000.0)
            .ToString("F3", CultureInfo.InvariantCulture);

        string duration =
            durationSeconds
            .ToString("F3", CultureInfo.InvariantCulture);

        if (padStartMs > 0)
        {
            string delay = $"{padStartMs}|{padStartMs}";
            return
                $"-y -ss {ss} -i \"{inputPath}\" " +
                $"-af \"adelay={delay},apad,atrim=0:{duration}\" " +
                $"\"{outputPath}\"";
        }
        return
            $"-y -ss {ss} -i \"{inputPath}\" " +
            $"-t {duration} " +
            $"\"{outputPath}\"";
    }

    private static async Task<double> ProbeDurationSecondsAsync(
        string videoPath,
        int fallbackSeconds)
    {
        string arguments =
            $"-v error " +
            $"-show_entries format=duration " +
            $"-of default=noprint_wrappers=1:nokey=1 " +
            $"\"{videoPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FFMpegCore.GlobalFFOptions.GetFFProbeBinaryPath(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        string durationText = await process.StandardOutput.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(5000));

        if (double.TryParse(
                durationText.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double duration))
        {
            return duration;
        }
        return fallbackSeconds;
    }

    private static Process CreateFFmpegProcess(
        string ffmpegPath,
        string arguments,
        bool redirectOutput)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = redirectOutput,
                CreateNoWindow = true
            }
        };
    }
    // ...) FFmpeg обробка

    // Сповіщення про зміну пристрою (...
    private void StartDeviceChangeMonitoring()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceChangeCallback = new AudioDeviceChangeCallback(this);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceChangeCallback);
    }

    private void StopDeviceChangeMonitoring()
    {
        if (_deviceEnumerator == null ||
            _deviceChangeCallback == null)
        {
            return;
        }

        try
        {
            _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceChangeCallback);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Unregister audio device callback warning: {ex.Message}");
        }
        _deviceEnumerator.Dispose();
        _deviceEnumerator = null;
        _deviceChangeCallback = null;
    }

    private sealed class AudioDeviceChangeCallback : IMMNotificationClient
    {
        private readonly AudioRecorder _recorder;
        public AudioDeviceChangeCallback(AudioRecorder recorder)
        {
            _recorder = recorder;
        }

        public void OnDefaultDeviceChanged(
            DataFlow flow,
            Role role,
            string defaultDeviceId)
        {
            AppLogger.Info(
                $"Default audio device changed | Flow={flow} | Role={role} | Id={defaultDeviceId}");

            if (flow == DataFlow.Render &&
                role == Role.Multimedia)
            {
                _recorder.DefaultDeviceChanged?.Invoke(defaultDeviceId);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
    // ...) Сповіщення про зміну пристрою

    // Очищення (...
    private static void CleanupOldTempFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), "eventcapture_audio_*.wav"))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Audio temp cleanup warning: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
    // ...) Очищення
}
