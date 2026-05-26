using System.Runtime.InteropServices;
namespace EventCapture.App;

// Реєстрація глобальних гарячих клавіш через WinAPI RegisterHotKey.
public class HotkeyManager : IDisposable
{
    // WinAPI (...
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
    // ...) WinAPI

    // Модифікатори клавіш
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // Ідентифікатори hotkeys
    public const int HOTKEY_SCREENSHOT = 1;
    public const int HOTKEY_SAVE_VIDEO = 2;
    public const int HOTKEY_TOGGLE_OVERLAY = 3;

    // Стан менеджера
    private readonly IntPtr _windowHandle;

    // Ініціалізація
    public HotkeyManager(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    // Реєстрація hotkeys (...
    public void RegisterAll(
        string screenshotHotkey,
        string saveVideoHotkey,
        string toggleOverlayHotkey)
    {
        UnregisterAll();
        Register(HOTKEY_SCREENSHOT, screenshotHotkey);
        Register(HOTKEY_SAVE_VIDEO, saveVideoHotkey);
        Register(HOTKEY_TOGGLE_OVERLAY, toggleOverlayHotkey);
    }

    private void Register(int id, string hotkey)
    {
        var (modifiers, virtualKey) = Parse(hotkey);

        RegisterHotKey(
            _windowHandle,
            id,
            modifiers,
            virtualKey);
    }

    public void UnregisterAll()
    {
        UnregisterHotKey(
            _windowHandle,
            HOTKEY_SCREENSHOT);

        UnregisterHotKey(
            _windowHandle,
            HOTKEY_SAVE_VIDEO);

        UnregisterHotKey(
            _windowHandle,
            HOTKEY_TOGGLE_OVERLAY);
    }
    // ...) Реєстрація hotkeys

    // Парсинг hotkeys (...
    public static (uint modifiers, uint virtualKey) Parse(string hotkey)
    {
        uint modifiers = 0;
        uint virtualKey = 0;
        var parts = hotkey.Split('+');

        foreach (var part in parts)
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "alt":
                    modifiers |= MOD_ALT;
                    break;

                case "ctrl":
                    modifiers |= MOD_CTRL;
                    break;

                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;

                case "win":
                    modifiers |= MOD_WIN;
                    break;

                default:
                    if (Enum.TryParse<Keys>(
                        part.Trim(),
                        true,
                        out var key))
                    {
                        virtualKey = (uint)key;
                    }
                    break;
            }
        }
        return (modifiers, virtualKey);
    }
    // ...) Парсинг hotkeys

    // Звільнення ресурсів (...
    public void Dispose()
    {
        UnregisterAll();
    }
    // ...) Звільнення ресурсів
}