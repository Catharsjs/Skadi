using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

// Паралельний запис системного звуку і мікрофону через NAudio WASAPI
// Системний звук — WasapiLoopbackCapture (не потребує Stereo Mix)
// Мікрофон — WasapiCapture
public class AudioRecorder : IDisposable
{
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _loopbackWriter;
    private WaveFileWriter? _micWriter;

    private DateTime _recordingStartTime;
    private string _loopbackTempPath = string.Empty;
    private string _micTempPath = string.Empty;

    private readonly System.Diagnostics.Stopwatch _recordingStopwatch = new();
    private readonly System.Diagnostics.Stopwatch _firstRecordingStopwatch = new();

    // Timestamps used to align audio with the video encoder timeline.
    private long _firstCaptureTimestamp = 0;
    private long _sharedStartTimestamp = 0;
    private long _audioActualStartTimestamp = 0;
    private long _firstBufferDurationMs = 0;
    private long _firstFrameTimestamp = 0;

    private readonly object _loopbackLock = new();
    private readonly object _micLock = new();
    private readonly object _segmentsLock = new();

    private MMDeviceEnumerator? _deviceEnumerator;
    private AudioDeviceChangeCallback? _deviceChangeCallback;

    public event Action<string>? DefaultDeviceChanged;

    // Залишаємо для логів/UI/MainForm.
    public readonly List<string> LoopbackTempPaths = new();

    // Реальна структура для коректної синхронізації після перемикання audio device.
    private class AudioSegment
    {
        public string Path { get; set; } = string.Empty;
        public long StartTimestamp { get; set; }
        public long ActualStartTimestamp { get; set; }
        public long FirstFrameTimestamp { get; set; }
        public long FirstBufferDurationMs { get; set; }
        public string DeviceName { get; set; } = string.Empty;
    }

    private readonly List<AudioSegment> _loopbackSegments = new();
    private readonly List<AudioSegment> _micSegments = new();

    public bool IsRecordingSystem { get; private set; }
    public bool IsRecordingMic { get; private set; }
    public bool UseDefaultSystemDevice { get; set; } = true;
    public bool UseDefaultMicDevice { get; set; } = true;

    // ─── Отримання списку пристроїв ───────────────────────────────────────

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var devices = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            devices.Add((device.ID, device.FriendlyName));

        return devices;
    }

    public static List<(string Id, string Name)> GetInputDevices()
    {
        var devices = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            devices.Add((device.ID, device.FriendlyName));

        return devices;
    }

    // ─── Запуск запису ────────────────────────────────────────────────────

    public void StartRecording(
        bool recordSystem,
        string? systemDeviceId,
        bool recordMic,
        string? micDeviceId,
        long sharedStartTimestamp = 0)
    {
        StopDeviceChangeMonitoring();

        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceChangeCallback = new AudioDeviceChangeCallback(this);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceChangeCallback);

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
        _firstFrameTimestamp = 0;
        _firstBufferDurationMs = 0;
        _firstCaptureTimestamp = 0;

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

    private void StartSystemCapture(string? deviceId)
    {
        string logPath = GetLogPath();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"eventcapture_audio_system_{Guid.NewGuid()}.wav");

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

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] StartSystemCapture: {device.FriendlyName}\n" +
                $"  TempPath: {tempPath}\n" +
                $"  SegmentStartTimestamp: {segment.StartTimestamp}\n" +
                $"  LoopbackTempPaths count: {LoopbackTempPaths.Count}\n" +
                $"  LoopbackSegments count: {_loopbackSegments.Count}\n");

            int frameCount = 0;

            capture.DataAvailable += (s, e) =>
            {
                lock (_loopbackLock)
                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);

                frameCount++;

                if (frameCount == 1)
                {
                    long firstFrameTimestamp = Environment.TickCount64;
                    long firstBufferDurationMs = 0;

                    var format = capture.WaveFormat;
                    if (format != null)
                    {
                        firstBufferDurationMs = (long)(
                            e.BytesRecorded * 1000.0 /
                            (format.SampleRate * format.Channels * (format.BitsPerSample / 8))
                        );
                    }

                    _firstFrameTimestamp = firstFrameTimestamp;
                    _firstBufferDurationMs = firstBufferDurationMs;

                    segment.FirstFrameTimestamp = firstFrameTimestamp;
                    segment.FirstBufferDurationMs = firstBufferDurationMs;

                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] DataAvailable: frame=1, bytes={e.BytesRecorded}\n" +
                        $"  device: {segment.DeviceName}\n" +
                        $"  segmentPath: {segment.Path}\n" +
                        $"  segmentStartTimestamp: {segment.StartTimestamp}\n" +
                        $"  firstFrameTimestamp: {segment.FirstFrameTimestamp}\n" +
                        $"  firstBufferDurationMs: {segment.FirstBufferDurationMs}\n" +
                        $"  format: {format?.SampleRate}Hz, {format?.Channels}ch, {format?.BitsPerSample}bit\n" +
                        $"  delayFromAudioStart: {segment.FirstFrameTimestamp - segment.ActualStartTimestamp}ms\n" +
                        $"  delayFromVideoStart: {segment.FirstFrameTimestamp - _sharedStartTimestamp}ms\n");
                }
                else if (frameCount % 100 == 0)
                {
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] DataAvailable: frame={frameCount}, bytes={e.BytesRecorded}\n");
                }
            };

            capture.StartRecording();

            long actualStartTimestamp = Environment.TickCount64;
            _audioActualStartTimestamp = actualStartTimestamp;
            segment.ActualStartTimestamp = actualStartTimestamp;

            File.AppendAllText(logPath,
                $"  _sharedStartTimestamp: {_sharedStartTimestamp}\n" +
                $"  _audioActualStartTimestamp: {_audioActualStartTimestamp}\n" +
                $"  segmentActualStartTimestamp: {segment.ActualStartTimestamp}\n" +
                $"  audioDelay: {_audioActualStartTimestamp - _sharedStartTimestamp}ms\n");

            if (_firstCaptureTimestamp == 0)
                _firstCaptureTimestamp = Environment.TickCount64;

            _recordingStopwatch.Restart();

            if (!_firstRecordingStopwatch.IsRunning)
                _firstRecordingStopwatch.Restart();

            IsRecordingSystem = true;

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] Recording started on: {device.FriendlyName}\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] StartSystemCapture ERROR: {ex.Message}\n");

            IsRecordingSystem = false;
        }
    }

    private void StartMicCapture(string? deviceId)
    {
        string logPath = GetLogPath();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"eventcapture_audio_mic_{Guid.NewGuid()}.wav");

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

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] StartMicCapture: {device.FriendlyName}\n" +
                $"  TempPath: {tempPath}\n" +
                $"  SegmentStartTimestamp: {segment.StartTimestamp}\n" +
                $"  MicSegments count: {_micSegments.Count}\n");

            int frameCount = 0;

            capture.DataAvailable += (s, e) =>
            {
                lock (_micLock)
                    _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);

                frameCount++;

                if (frameCount == 1)
                {
                    long firstFrameTimestamp = Environment.TickCount64;
                    long firstBufferDurationMs = 0;

                    var format = capture.WaveFormat;
                    if (format != null)
                    {
                        firstBufferDurationMs = (long)(
                            e.BytesRecorded * 1000.0 /
                            (format.SampleRate * format.Channels * (format.BitsPerSample / 8))
                        );
                    }

                    segment.FirstFrameTimestamp = firstFrameTimestamp;
                    segment.FirstBufferDurationMs = firstBufferDurationMs;

                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] Mic DataAvailable: frame=1, bytes={e.BytesRecorded}\n" +
                        $"  device: {segment.DeviceName}\n" +
                        $"  segmentPath: {segment.Path}\n" +
                        $"  segmentStartTimestamp: {segment.StartTimestamp}\n" +
                        $"  firstFrameTimestamp: {segment.FirstFrameTimestamp}\n" +
                        $"  firstBufferDurationMs: {segment.FirstBufferDurationMs}\n" +
                        $"  format: {format?.SampleRate}Hz, {format?.Channels}ch, {format?.BitsPerSample}bit\n" +
                        $"  delayFromMicStart: {segment.FirstFrameTimestamp - segment.ActualStartTimestamp}ms\n" +
                        $"  delayFromVideoStart: {segment.FirstFrameTimestamp - _sharedStartTimestamp}ms\n");
                }
            };

            capture.StartRecording();

            long actualStartTimestamp = Environment.TickCount64;
            segment.ActualStartTimestamp = actualStartTimestamp;

            File.AppendAllText(logPath,
                $"  micActualStartTimestamp: {segment.ActualStartTimestamp}\n" +
                $"  micDelay: {segment.ActualStartTimestamp - _sharedStartTimestamp}ms\n");

            _recordingStartTime = DateTime.Now;
            IsRecordingMic = true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] StartMicCapture ERROR: {ex.Message}\n");

            IsRecordingMic = false;
        }
    }

    public async Task<string?> SaveLastSecondsAsync(
        string outputFolder,
        int seconds,
        string videoPath,
        long videoElapsedMs,
        long videoStartTimestamp)
    {
        long capturedAudioActualStartTimestamp = _audioActualStartTimestamp;
        long capturedSharedStartTimestamp = _sharedStartTimestamp;

        List<AudioSegment> existingSegments;
        List<AudioSegment> existingMicSegments;

        lock (_segmentsLock)
        {
            existingSegments = _loopbackSegments
                .Where(s => File.Exists(s.Path))
                .Select(CloneSegment)
                .ToList();

            existingMicSegments = _micSegments
                .Where(s => File.Exists(s.Path))
                .Select(CloneSegment)
                .ToList();
        }

        StopCapture();
        await Task.Delay(300);

        string logPath = GetLogPath();

        File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] SaveLastSecondsAsync\n" +
            $"  LoopbackTempPaths count: {LoopbackTempPaths.Count}\n" +
            $"  LoopbackSegments existing count: {existingSegments.Count}\n" +
            $"  MicPath exists: {File.Exists(_micTempPath)}, size: {(File.Exists(_micTempPath) ? new FileInfo(_micTempPath).Length : 0)}\n");

        foreach (var p in LoopbackTempPaths)
        {
            File.AppendAllText(logPath,
                $"  loopback path: exists={File.Exists(p)}, size={(File.Exists(p) ? new FileInfo(p).Length : 0)}, path={p}\n");
        }

        foreach (var segment in existingSegments)
        {
            File.AppendAllText(logPath,
                $"  segment: exists={File.Exists(segment.Path)}, size={(File.Exists(segment.Path) ? new FileInfo(segment.Path).Length : 0)}\n" +
                $"    device={segment.DeviceName}\n" +
                $"    path={segment.Path}\n" +
                $"    start={segment.StartTimestamp}\n" +
                $"    actualStart={segment.ActualStartTimestamp}\n" +
                $"    firstFrame={segment.FirstFrameTimestamp}\n" +
                $"    firstBufferMs={segment.FirstBufferDurationMs}\n");
        }

        File.AppendAllText(logPath,
            $"  videoElapsedMs: {videoElapsedMs}\n" +
            $"  videoStartTimestamp: {videoStartTimestamp}\n");

        if (existingSegments.Count == 0 && existingMicSegments.Count == 0)
            return null;

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        double videoDuration = await ProbeDurationSecondsAsync(videoPath, seconds);
        long realVideoDurationMs = (long)Math.Round(videoDuration * 1000.0);

        long videoSegmentStartOffsetMs = videoElapsedMs - realVideoDurationMs;
        if (videoSegmentStartOffsetMs < 0)
            videoSegmentStartOffsetMs = 0;

        var trimmedAudio = new List<string>();

        foreach (var segment in existingSegments)
        {
            string? trimmed = await TrimAudioSegmentAsync(
                ffmpegPath,
                logPath,
                segment,
                videoPath,
                videoStartTimestamp,
                videoDuration,
                realVideoDurationMs,
                videoSegmentStartOffsetMs);

            if (!string.IsNullOrEmpty(trimmed))
                trimmedAudio.Add(trimmed);
        }

        foreach (var segment in existingMicSegments)
        {
            string? trimmed = await TrimAudioSegmentAsync(
                ffmpegPath,
                logPath,
                segment,
                videoPath,
                videoStartTimestamp,
                videoDuration,
                realVideoDurationMs,
                videoSegmentStartOffsetMs);

            if (!string.IsNullOrEmpty(trimmed))
                trimmedAudio.Add(trimmed);
        }
        if (trimmedAudio.Count == 0)
            return null;

        string outputPath = Path.Combine(
            outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" +
            Guid.NewGuid().ToString("N")[..8] +
            "_final.mp4");

        string audioInputArgs = string.Join(" ", trimmedAudio.Select(p => $"-i \"{p}\""));

        string audioFilter = trimmedAudio.Count > 1
            ? $"-filter_complex amix=inputs={trimmedAudio.Count}:duration=longest:dropout_transition=0 -c:a aac"
            : "-c:a aac";

        var mergeArgs = $"-y -i \"{videoPath}\" {audioInputArgs} -c:v copy {audioFilter} -shortest \"{outputPath}\"";

        var mergeProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = mergeArgs,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        mergeProcess.Start();
        string mergeError = await mergeProcess.StandardError.ReadToEndAsync();
        await Task.Run(() => mergeProcess.WaitForExit(30000));

        File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] Merge result: exists={File.Exists(outputPath)}, size={(File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0)}\n" +
            $"MergeError: {mergeError}\n");

        foreach (var segment in existingSegments)
            TryDelete(segment.Path);

        foreach (var segment in existingMicSegments)
            TryDelete(segment.Path);

        foreach (var p in trimmedAudio)
            TryDelete(p);

        lock (_segmentsLock)
        {
            LoopbackTempPaths.Clear();
            _loopbackSegments.Clear();
            _micSegments.Clear();
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return null;

        return outputPath;
    }
    private static AudioSegment CloneSegment(AudioSegment s)
    {
        return new AudioSegment
        {
            Path = s.Path,
            StartTimestamp = s.StartTimestamp,
            ActualStartTimestamp = s.ActualStartTimestamp,
            FirstFrameTimestamp = s.FirstFrameTimestamp,
            FirstBufferDurationMs = s.FirstBufferDurationMs,
            DeviceName = s.DeviceName
        };
    }

    private static async Task<string?> TrimAudioSegmentAsync(
        string ffmpegPath,
        string logPath,
        AudioSegment segment,
        string videoPath,
        long videoStartTimestamp,
        double videoDuration,
        long realVideoDurationMs,
        long videoSegmentStartOffsetMs)
    {
        if (!File.Exists(segment.Path))
            return null;

        string trimmed = Path.Combine(
            Path.GetTempPath(),
            $"eventcapture_audio_trim_{Guid.NewGuid()}.wav");

        long audioFileStartRelativeToVideoMs = CalculateAudioFileStartRelativeToVideoMs(
            segment.FirstFrameTimestamp,
            segment.FirstBufferDurationMs,
            segment.ActualStartTimestamp > 0 ? segment.ActualStartTimestamp : segment.StartTimestamp,
            videoStartTimestamp);

        long ssStartMs = videoSegmentStartOffsetMs - audioFileStartRelativeToVideoMs;
        long padStartMs = 0;

        if (ssStartMs < 0)
        {
            padStartMs = -ssStartMs;
            ssStartMs = 0;
        }

        var ssStr = (ssStartMs / 1000.0)
            .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        var durationStr = videoDuration
            .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        string trimArgs;

        if (padStartMs > 0)
        {
            string delay = $"{padStartMs}|{padStartMs}";

            trimArgs =
                $"-y -ss {ssStr} -i \"{segment.Path}\" " +
                $"-af \"adelay={delay},apad,atrim=0:{durationStr}\" \"{trimmed}\"";
        }
        else
        {
            trimArgs =
                $"-y -ss {ssStr} -i \"{segment.Path}\" " +
                $"-t {durationStr} \"{trimmed}\"";
        }

        File.AppendAllText(logPath,
            $"  TrimAudioSegment\n" +
            $"    device: {segment.DeviceName}\n" +
            $"    path: {segment.Path}\n" +
            $"    realVideoDurationMs: {realVideoDurationMs}\n" +
            $"    videoSegmentStartOffsetMs: {videoSegmentStartOffsetMs}\n" +
            $"    audioFileStartRelativeToVideoMs: {audioFileStartRelativeToVideoMs}\n" +
            $"    ssStartMs: {ssStartMs}\n" +
            $"    padStartMs: {padStartMs}\n" +
            $"    segmentStartTimestamp: {segment.StartTimestamp}\n" +
            $"    segmentActualStartTimestamp: {segment.ActualStartTimestamp}\n" +
            $"    firstBufferDurationMs: {segment.FirstBufferDurationMs}\n" +
            $"    firstFrameTimestamp: {segment.FirstFrameTimestamp}\n" +
            $"    videoDuration: {videoDuration:F3}\n" +
            $"    trimArgs: {trimArgs}\n");

        var trimProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = trimArgs,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        trimProcess.Start();
        string trimError = await trimProcess.StandardError.ReadToEndAsync();
        await Task.Run(() => trimProcess.WaitForExit(15000));

        File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] Trim segment result: exists={File.Exists(trimmed)}, size={(File.Exists(trimmed) ? new FileInfo(trimmed).Length : 0)}\n" +
            $"TrimError: {trimError}\n");

        if (File.Exists(trimmed))
        {
            var length = new FileInfo(trimmed).Length;

            // 78 bytes = майже порожній WAV header без аудіо
            // Відсікаємо дуже маленькі/биті сегменти.
            if (length > 4096)
                return trimmed;

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] Trim segment skipped: file too small, size={length}, path={trimmed}\n");
        }

        TryDelete(trimmed);
        return null;
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
            // The first DataAvailable timestamp is at the end of the first captured buffer,
            // so subtract the buffer duration to estimate the timestamp of audio sample zero.
            return firstFrameTimestamp - firstBufferDurationMs - videoStartTimestamp;
        }

        if (audioActualStartTimestamp > 0)
            return audioActualStartTimestamp - videoStartTimestamp;

        return 0;
    }

    private static async Task<double> ProbeDurationSecondsAsync(string videoPath, int fallbackSeconds)
    {
        var probeArgs = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";

        var probeProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = FFMpegCore.GlobalFFOptions.GetFFProbeBinaryPath(),
                Arguments = probeArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        probeProcess.Start();
        string durationStr = await probeProcess.StandardOutput.ReadToEndAsync();
        await Task.Run(() => probeProcess.WaitForExit(5000));

        if (!double.TryParse(
                durationStr.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double videoDuration))
        {
            videoDuration = fallbackSeconds;
        }

        return videoDuration;
    }

    private void StopCapture()
    {
        StopSystemCaptureOnly();
        StopMicCaptureOnly();

        IsRecordingSystem = false;
        IsRecordingMic = false;

        _firstFrameTimestamp = 0;
        _firstBufferDurationMs = 0;
        _firstCaptureTimestamp = 0;
        _audioActualStartTimestamp = 0;

        _firstRecordingStopwatch.Reset();

        StopDeviceChangeMonitoring();
    }

    private void StopSystemCaptureOnly()
    {
        if (_loopbackCapture != null)
        {
            try { _loopbackCapture.StopRecording(); } catch { }
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

    private void StopMicCaptureOnly()
    {
        if (_micCapture != null)
        {
            try { _micCapture.StopRecording(); } catch { }
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

    private void StopDeviceChangeMonitoring()
    {
        if (_deviceEnumerator != null && _deviceChangeCallback != null)
        {
            try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceChangeCallback); } catch { }
            _deviceEnumerator.Dispose();
            _deviceEnumerator = null;
            _deviceChangeCallback = null;
        }
    }

    private static void CleanupOldTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var file in Directory.GetFiles(tempDir, "eventcapture_audio_*.wav"))
            {
                TryDelete(file);
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string GetLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EventCapture",
            "full_debug.log");
    }

    public void Dispose() => StopCapture();

    private class AudioDeviceChangeCallback : IMMNotificationClient
    {
        private readonly AudioRecorder _recorder;

        public AudioDeviceChangeCallback(AudioRecorder recorder)
        {
            _recorder = recorder;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            File.AppendAllText(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "EventCapture",
                    "device_change.log"),
                $"[{DateTime.Now:HH:mm:ss}] OnDefaultDeviceChanged: flow={flow}, role={role}, id={defaultDeviceId}\n");

            if (flow == DataFlow.Render && role == Role.Multimedia)
                _recorder.DefaultDeviceChanged?.Invoke(defaultDeviceId);
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}