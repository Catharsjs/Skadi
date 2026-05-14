using System.Text.Json;

namespace EventCapture.App;

// Зберігає і завантажує налаштування користувача
// Файл: %AppData%\EventCapture\settings.json
public class AppSettings
{
    // ─── Налаштування відео ───────────────────────────────────────────────
    public int Fps { get; set; } = 60;
    public int BufferSeconds { get; set; } = 60;
    public string Resolution { get; set; } = "Native";

    // ─── Налаштування аудіо ───────────────────────────────────────────────
    public bool RecordSystemAudio { get; set; } = false;
    public bool RecordMicrophone { get; set; } = false;
    public string? SystemAudioDeviceId { get; set; } = null;
    public string? MicDeviceId { get; set; } = null;

    // ─── Хоткеї ──────────────────────────────────────────────────────────
    public string HotkeyScreenshot { get; set; } = "Alt+F1";
    public string HotkeySaveVideo { get; set; } = "Alt+F2";
    public string HotkeyToggleUI { get; set; } = "Alt+F3";

    // ─── Загальні ─────────────────────────────────────────────────────────
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EventCapture");

    private static readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EventCapture", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    // ─── Автозапуск через реєстр Windows ─────────────────────────────────
    private static readonly string _appName = "EventCapture";
    private static readonly string _appPath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

    public static bool IsAutoStartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(_appName) != null;
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (enable)
            key?.SetValue(_appName, $"\"{_appPath}\"");
        else
            key?.DeleteValue(_appName, false);
    }
}