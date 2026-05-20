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

    private MMDeviceEnumerator? _deviceEnumerator;
    private AudioDeviceChangeCallback? _deviceChangeCallback;

    public event Action<string>? DefaultDeviceChanged;

    public readonly List<string> LoopbackTempPaths = new();

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

        // When the output device changes, the new loopback file starts later than the video timeline.
        // SaveLastSecondsAsync will compensate this by using the first audio frame timestamp.
        StartSystemCapture(deviceId);
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

            _loopbackTempPath = Path.Combine(
                Path.GetTempPath(),
                $"eventcapture_audio_system_{Guid.NewGuid()}.wav");

            LoopbackTempPaths.Add(_loopbackTempPath);

            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] StartSystemCapture: {device.FriendlyName}\n" +
                $"  TempPath: {_loopbackTempPath}\n" +
                $"  LoopbackTempPaths count: {LoopbackTempPaths.Count}\n");

            _loopbackCapture = new WasapiLoopbackCapture(device);
            _loopbackWriter = new WaveFileWriter(_loopbackTempPath, _loopbackCapture.WaveFormat);

            int frameCount = 0;

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                lock (_loopbackLock)
                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);

                frameCount++;

                if (frameCount == 1)
                {
                    _firstFrameTimestamp = Environment.TickCount64;

                    var format = _loopbackCapture?.WaveFormat;
                    if (format != null)
                    {
                        _firstBufferDurationMs = (long)(
                            e.BytesRecorded * 1000.0 /
                            (format.SampleRate * format.Channels * (format.BitsPerSample / 8))
                        );
                    }

                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] DataAvailable: frame=1, bytes={e.BytesRecorded}\n" +
                        $"  firstFrameTimestamp: {_firstFrameTimestamp}\n" +
                        $"  firstBufferDurationMs: {_firstBufferDurationMs}\n" +
                        $"  format: {format?.SampleRate}Hz, {format?.Channels}ch, {format?.BitsPerSample}bit\n" +
                        $"  delayFromAudioStart: {_firstFrameTimestamp - _audioActualStartTimestamp}ms\n" +
                        $"  delayFromVideoStart: {_firstFrameTimestamp - _sharedStartTimestamp}ms\n");
                }
                else if (frameCount % 100 == 0)
                {
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] DataAvailable: frame={frameCount}, bytes={e.BytesRecorded}\n");
                }
            };

            _loopbackCapture.StartRecording();
            _audioActualStartTimestamp = Environment.TickCount64;

            File.AppendAllText(logPath,
                $"  _sharedStartTimestamp: {_sharedStartTimestamp}\n" +
                $"  _audioActualStartTimestamp: {_audioActualStartTimestamp}\n" +
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
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            _micCapture = new WasapiCapture(device);
            _micTempPath = Path.Combine(
                Path.GetTempPath(),
                $"eventcapture_audio_mic_{Guid.NewGuid()}.wav");

            _micWriter = new WaveFileWriter(_micTempPath, _micCapture.WaveFormat);

            _micCapture.DataAvailable += (s, e) =>
            {
                lock (_micLock)
                    _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _micCapture.StartRecording();
            _recordingStartTime = DateTime.Now;
            IsRecordingMic = true;
        }
        catch
        {
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
        long capturedFirstFrameTimestamp = _firstFrameTimestamp;
        long capturedFirstBufferDurationMs = _firstBufferDurationMs;
        long capturedAudioActualStartTimestamp = _audioActualStartTimestamp;
        long capturedSharedStartTimestamp = _sharedStartTimestamp;

        StopCapture();
        await Task.Delay(300);

        var audioInputs = new List<string>();
        string logPath = GetLogPath();

        File.AppendAllText(logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] SaveLastSecondsAsync\n" +
            $"  LoopbackTempPaths count: {LoopbackTempPaths.Count}\n" +
            $"  MicPath exists: {File.Exists(_micTempPath)}, size: {(File.Exists(_micTempPath) ? new FileInfo(_micTempPath).Length : 0)}\n");

        foreach (var p in LoopbackTempPaths)
        {
            File.AppendAllText(logPath,
                $"  loopback: exists={File.Exists(p)}, size={(File.Exists(p) ? new FileInfo(p).Length : 0)}, path={p}\n");
        }

        File.AppendAllText(logPath,
            $"  videoElapsedMs: {videoElapsedMs}\n" +
            $"  videoStartTimestamp: {videoStartTimestamp}\n");

        if (LoopbackTempPaths.Count > 0)
        {
            var existingPaths = LoopbackTempPaths.Where(File.Exists).ToList();

            if (existingPaths.Count == 1)
            {
                audioInputs.Add(existingPaths[0]);
            }
            else if (existingPaths.Count > 1)
            {
                string mergedLoopback = Path.Combine(
                    Path.GetTempPath(),
                    $"eventcapture_audio_system_merged_{Guid.NewGuid()}.wav");

                var concatInputs = string.Join(" ", existingPaths.Select(p => $"-i \"{p}\""));
                var concatArgs = $"-y {concatInputs} -filter_complex concat=n={existingPaths.Count}:v=0:a=1 \"{mergedLoopback}\"";

                var concatProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath(),
                        Arguments = concatArgs,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                concatProcess.Start();
                string concatError = await concatProcess.StandardError.ReadToEndAsync();
                await Task.Run(() => concatProcess.WaitForExit(30000));

                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] Concat result: exists={File.Exists(mergedLoopback)}, size={(File.Exists(mergedLoopback) ? new FileInfo(mergedLoopback).Length : 0)}\n" +
                    $"Error: {concatError}\n");

                if (File.Exists(mergedLoopback) && new FileInfo(mergedLoopback).Length > 0)
                    audioInputs.Add(mergedLoopback);
            }
        }

        if (File.Exists(_micTempPath))
            audioInputs.Add(_micTempPath);

        if (audioInputs.Count == 0)
            return null;

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        double videoDuration = await ProbeDurationSecondsAsync(videoPath, seconds);

        var trimmedAudio = new List<string>();

        foreach (var audioPath in audioInputs)
        {
            string trimmed = Path.Combine(
                Path.GetTempPath(),
                $"eventcapture_audio_trim_{Guid.NewGuid()}.wav");

            long realVideoDurationMs = (long)Math.Round(videoDuration * 1000.0);

            // Actual beginning of the already-trimmed video segment on the original encoder timeline.
            // This must be based on real video duration because ffmpeg -c:v copy may cut at keyframes
            // and produce e.g. 30.783s instead of exactly 30.000s.
            long videoSegmentStartOffsetMs = videoElapsedMs - realVideoDurationMs;
            if (videoSegmentStartOffsetMs < 0)
                videoSegmentStartOffsetMs = 0;

            long audioFileStartRelativeToVideoMs = CalculateAudioFileStartRelativeToVideoMs(
                capturedFirstFrameTimestamp,
                capturedFirstBufferDurationMs,
                capturedAudioActualStartTimestamp,
                videoStartTimestamp);

            long ssStartMs = videoSegmentStartOffsetMs - audioFileStartRelativeToVideoMs;
            long padStartMs = 0;

            // If the audio file starts after the video segment, ffmpeg cannot seek before audio zero.
            // Add silence to the beginning instead.
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
                    $"-y -ss {ssStr} -i \"{audioPath}\" " +
                    $"-af \"adelay={delay},apad,atrim=0:{durationStr}\" \"{trimmed}\"";
            }
            else
            {
                trimArgs =
                    $"-y -ss {ssStr} -i \"{audioPath}\" " +
                    $"-t {durationStr} \"{trimmed}\"";
            }

            File.AppendAllText(logPath,
                $"  realVideoDurationMs: {realVideoDurationMs}\n" +
                $"  videoSegmentStartOffsetMs: {videoSegmentStartOffsetMs}\n" +
                $"  audioFileStartRelativeToVideoMs: {audioFileStartRelativeToVideoMs}\n" +
                $"  ssStartMs: {ssStartMs}\n" +
                $"  padStartMs: {padStartMs}\n" +
                $"  firstBufferDurationMs: {capturedFirstBufferDurationMs}\n" +
                $"  firstFrameTimestamp: {capturedFirstFrameTimestamp}\n" +
                $"  sharedStartTimestamp: {capturedSharedStartTimestamp}\n" +
                $"  audioActualStartTimestamp: {capturedAudioActualStartTimestamp}\n" +
                $"  videoDuration: {videoDuration:F3}\n" +
                $"  trimArgs: {trimArgs}\n");

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
                $"[{DateTime.Now:HH:mm:ss.fff}] Trim result: exists={File.Exists(trimmed)}, size={(File.Exists(trimmed) ? new FileInfo(trimmed).Length : 0)}\n" +
                $"TrimError: {trimError}\n");

            if (File.Exists(trimmed) && new FileInfo(trimmed).Length > 0)
                trimmedAudio.Add(trimmed);
        }

        if (trimmedAudio.Count == 0)
            return null;

        string outputPath = Path.Combine(
            outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");

        string audioInputArgs = string.Join(" ", trimmedAudio.Select(p => $"-i \"{p}\""));
        string audioFilter = trimmedAudio.Count > 1
            ? $"-filter_complex amix=inputs={trimmedAudio.Count}:duration=shortest -c:a aac"
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

        foreach (var p in audioInputs)
            TryDelete(p);

        foreach (var p in trimmedAudio)
            TryDelete(p);

        LoopbackTempPaths.Clear();

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return null;

        return outputPath;
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
