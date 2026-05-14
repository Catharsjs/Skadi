using NAudio.CoreAudioApi;
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

    private string _loopbackTempPath = string.Empty;
    private string _micTempPath = string.Empty;

    public bool IsRecordingSystem { get; private set; }
    public bool IsRecordingMic { get; private set; }

    private readonly object _loopbackLock = new();
    private readonly object _micLock = new();

    // ─── Отримання списку пристроїв ───────────────────────────────────────

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var devices = new List<(string, string)>();
        var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            devices.Add((device.ID, device.FriendlyName));
        return devices;
    }

    public static List<(string Id, string Name)> GetInputDevices()
    {
        var devices = new List<(string, string)>();
        var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            devices.Add((device.ID, device.FriendlyName));
        return devices;
    }

    // ─── Запуск запису ────────────────────────────────────────────────────

    public void StartRecording(bool recordSystem, string? systemDeviceId,
        bool recordMic, string? micDeviceId)
    {
        CleanupOldTempFiles();

        if (recordSystem)
            StartSystemCapture(systemDeviceId);

        if (recordMic)
            StartMicCapture(micDeviceId);
    }

    private void StartSystemCapture(string? deviceId)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _loopbackCapture = new WasapiLoopbackCapture(device);
            _loopbackTempPath = Path.Combine(Path.GetTempPath(),
                $"eventcapture_audio_system_{Guid.NewGuid()}.wav");
            _loopbackWriter = new WaveFileWriter(_loopbackTempPath, _loopbackCapture.WaveFormat);

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                lock (_loopbackLock)
                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _loopbackCapture.StartRecording();
            IsRecordingSystem = true;
        }
        catch { IsRecordingSystem = false; }
    }

    private void StartMicCapture(string? deviceId)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = deviceId != null
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            _micCapture = new WasapiCapture(device);
            _micTempPath = Path.Combine(Path.GetTempPath(),
                $"eventcapture_audio_mic_{Guid.NewGuid()}.wav");
            _micWriter = new WaveFileWriter(_micTempPath, _micCapture.WaveFormat);

            _micCapture.DataAvailable += (s, e) =>
            {
                lock (_micLock)
                    _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _micCapture.StartRecording();
            IsRecordingMic = true;
        }
        catch { IsRecordingMic = false; }
    }

    // ─── Збереження останніх N секунд ────────────────────────────────────

    public async Task<string?> SaveLastSecondsAsync(string outputFolder, int seconds,
        string videoPath)
    {
        // Зупиняємо запис і закриваємо файли
        StopCapture();
        await Task.Delay(300);

        var audioInputs = new List<string>();

        if (IsRecordingSystem && File.Exists(_loopbackTempPath))
            audioInputs.Add(_loopbackTempPath);

        if (IsRecordingMic && File.Exists(_micTempPath))
            audioInputs.Add(_micTempPath);

        if (audioInputs.Count == 0) return null;

        string outputPath = Path.Combine(outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_audio.mp4");

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();

        // Будуємо FFmpeg команду
        string inputs = string.Join(" ", audioInputs.Select(p => $"-i \"{p}\""));
        string audioFilter = audioInputs.Count > 1
            ? $"-filter_complex amix=inputs={audioInputs.Count}:duration=shortest"
            : string.Empty;

        // Обрізаємо аудіо і мікшуємо з відео
        var args = $"-y -i \"{videoPath}\" {inputs} " +
                   $"-sseof -{seconds} " +
                   $"{audioFilter} " +
                   $"-c:v copy -c:a aac -shortest \"{outputPath}\"";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.StandardError.ReadToEndAsync();
        await Task.Run(() => process.WaitForExit(30000));

        foreach (var p in audioInputs)
            try { File.Delete(p); } catch { }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return null;

        return outputPath;
    }

    private void StopCapture()
    {
        if (_loopbackCapture != null)
        {
            _loopbackCapture.StopRecording();
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }

        lock (_loopbackLock)
        {
            _loopbackWriter?.Dispose();
            _loopbackWriter = null;
        }

        if (_micCapture != null)
        {
            _micCapture.StopRecording();
            _micCapture.Dispose();
            _micCapture = null;
        }

        lock (_micLock)
        {
            _micWriter?.Dispose();
            _micWriter = null;
        }

        IsRecordingSystem = false;
        IsRecordingMic = false;
    }

    private static void CleanupOldTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var file in Directory.GetFiles(tempDir, "eventcapture_audio_*.wav"))
                try { File.Delete(file); } catch { }
        }
        catch { }
    }

    public void Dispose() => StopCapture();
}