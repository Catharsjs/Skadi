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

    public readonly List<string> LoopbackTempPaths = new();
    public event Action<string>? DefaultDeviceChanged;
    public bool IsRecordingSystem { get; private set; }
    public bool IsRecordingMic { get; private set; }
    public bool UseDefaultSystemDevice { get; set; } = true;
    public bool UseDefaultMicDevice { get; set; } = true;

    private sealed class AudioSegment
    {
        public string Path { get; set; } = string.Empty;
        public long StartTimestamp { get; set; }
        public long ActualStartTimestamp { get; set; }
        public long FirstFrameTimestamp { get; set; }
        public long FirstBufferDurationMs { get; set; }
        public string DeviceName { get; set; } = string.Empty;
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
                lock (_loopbackLock)
                {
                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                }

                frameCount++;

                if (frameCount == 1)
                {
                    RegisterFirstAudioFrame(
                        segment,
                        capture.WaveFormat,
                        e.BytesRecorded,
                        "System");
                }
                else if (frameCount % 100 == 0)
                {
                    AppLogger.Debug($"System audio frame | Device={segment.DeviceName} | Frame={frameCount} | Bytes={e.BytesRecorded}");
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

        lock (_loopbackLock)
        {
            _loopbackWriter?.Flush();
            _loopbackWriter?.Dispose();
            _loopbackWriter = null;
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
                lock (_micLock)
                {
                    _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                }

                frameCount++;

                if (frameCount == 1)
                {
                    RegisterFirstAudioFrame(
                        segment,
                        capture.WaveFormat,
                        e.BytesRecorded,
                        "Microphone");
                }
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

        lock (_micLock)
        {
            _micWriter?.Flush();
            _micWriter?.Dispose();
            _micWriter = null;
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
        var existingSystemSegments = GetValidSystemSegmentsSnapshot();
        var existingMicSegments = GetValidMicSegmentsSnapshot();

        StopCapture();

        await Task.Delay(300);

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
            return null;

        string outputPath =
            await MergeAudioWithVideoAsync(
                ffmpegPath,
                outputFolder,
                videoPath,
                trimmedAudio);

        CleanupAfterSave(
            existingSystemSegments,
            existingMicSegments,
            trimmedAudio);

        if (!File.Exists(outputPath) ||
            new FileInfo(outputPath).Length == 0)
        {
            return null;
        }

        return outputPath;
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