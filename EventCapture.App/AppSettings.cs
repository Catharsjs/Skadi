using System.Text.Json;
using EventCapture.Core.Diagnostics;
namespace EventCapture.App;

// Зберігає і завантажує налаштування користувача.
public class AppSettings
{
    // Відео
    public int Fps { get; set; } = 60;
    public int BufferSeconds { get; set; } = 60;
    public string Resolution { get; set; } = "Native";

    // Аудіо
    public bool RecordSystemAudio { get; set; }
    public bool RecordMicrophone { get; set; }
    public string? SystemAudioDeviceId { get; set; }
    public string? MicDeviceId { get; set; }

    // Гарячі клавіші
    public string HotkeyScreenshot { get; set; } = "Alt+F1";
    public string HotkeySaveVideo { get; set; } = "Alt+F2";
    public string HotkeyToggleUI { get; set; } = "Alt+F3";


    // Загальні налаштування (...
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Skadi");
    // ...) Загальні налаштування


    // Системні шляхи (...
    private static readonly string _settingsPath = Path.Combine(
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

            return JsonSerializer.Deserialize<AppSettings>(json)
                   ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(AppSettings),
                $"Помилка завантаження налаштувань: {ex}");

            return new AppSettings();
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