using System.Text.Json;
using EventCapture.Core.Diagnostics;
using System.IO;
namespace EventCapture.App;

// Зберігає і завантажує налаштування користувача.
public class AppSettings
{
    // Відео
    public int Fps { get; set; } = 60;
    public int BufferSeconds { get; set; } = 60;
    public string Resolution { get; set; } = "Native";
    public bool BufferEnabled { get; set; }
    public string CaptureMode { get; set; } = "VideoAudio";
    public string CaptureTarget { get; set; } = "PrimaryMonitor";
    public int VideoQuality { get; set; } = 70;

    // Аудіо
    public bool RecordSystemAudio { get; set; }
    public bool RecordMicrophone { get; set; }
    public string? SystemAudioDeviceId { get; set; }
    public string? MicDeviceId { get; set; }
    public int SystemAudioVolume { get; set; } = 100;
    public int MicVolume { get; set; } = 100;
    public string HudMode { get; set; } = "None";

    // Гарячі клавіші
    public string HotkeyScreenshot { get; set; } = "Alt+F1";
    public string HotkeySaveVideo { get; set; } = "Alt+F3";
    public string HotkeyStartStopRecord { get; set; } = "Alt+F2";
    public string HotkeyToggleUI { get; set; } = "Alt+Z";


    // Загальні налаштування (...
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Skadi");
    // ...) Загальні налаштування


    // Системні шляхи (...
    private static readonly string _settingsPath =
        Environment.GetEnvironmentVariable("SKADI_SETTINGS_PATH") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Skadi",
            "settings.json");

    private static readonly string _appName = "Skadi";
    private static readonly string _appPath =
        System.Diagnostics.Process.GetCurrentProcess()
            .MainModule!
            .FileName;
    // ...) Системні шляхи


    // Завантаження та збереження налаштувань (...
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            var json = File.ReadAllText(_settingsPath);

            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                           ?? new AppSettings();
            using JsonDocument document = JsonDocument.Parse(json);
            bool hasHudMode = document.RootElement.TryGetProperty(nameof(HudMode), out _);
            if (!hasHudMode &&
                document.RootElement.TryGetProperty("ShowSystemInfo", out JsonElement legacySystemInfo) &&
                legacySystemInfo.ValueKind == JsonValueKind.True)
            {
                settings.HudMode = "System Info";
            }
            settings.Fps = NormalizeFps(settings.Fps);
            settings.VideoQuality = NormalizeVideoQuality(settings.VideoQuality);
            settings.HudMode = NormalizeHudMode(settings.HudMode);
            settings.NormalizeHotkeys();
            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(AppSettings),
                $"Помилка завантаження налаштувань: {ex}");

            return new AppSettings();
        }
    }

    private static int NormalizeVideoQuality(int value)
    {
        int[] presets = { 50, 70, 90 };
        return presets
            .OrderBy(preset => Math.Abs(preset - value))
            .First();
    }

    private static int NormalizeFps(int value)
    {
        int[] presets = { 30, 60 };
        return presets
            .OrderBy(preset => Math.Abs(preset - value))
            .First();
    }

    private static string NormalizeHudMode(string? value) => value switch
    {
        "Timer" => "Timer",
        "System Info" => "System Info",
        _ => "None"
    };

    private void NormalizeHotkeys()
    {
        if (string.Equals(HotkeySaveVideo, "Alt+F2", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(HotkeyStartStopRecord, "Alt+F3", StringComparison.OrdinalIgnoreCase))
        {
            HotkeySaveVideo = "Alt+F3";
            HotkeyStartStopRecord = "Alt+F2";
        }

        if (string.Equals(HotkeyToggleUI, "Alt+F3", StringComparison.OrdinalIgnoreCase))
        {
            HotkeyToggleUI = "Alt+Z";
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(_settingsPath)!);

            var json = JsonSerializer.Serialize(
                this,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(AppSettings),
                $"Помилка збереження налаштувань: {ex}");
        }
    }
    // ...) Завантаження та збереження налаштувань


    // Автозапуск через реєстр Windows (...
    public static bool IsAutoStartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");

        return key?.GetValue(_appName) != null;
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            true);

        if (enable)
        {
            key?.SetValue(
                _appName,
                $"\"{_appPath}\"");
        }
        else
        {
            key?.DeleteValue(
                _appName,
                false);
        }
    }
    // ...) Автозапуск через реєстр Windows
}
