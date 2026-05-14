using System.Runtime.InteropServices;

namespace EventCapture.App;

// Реєструє глобальні хоткеї через Win32 RegisterHotKey
// Підтримує комбінації з Alt, Ctrl, Shift, Win + будь-яка клавіша
public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_NONE = 0x0000;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    public const int HOTKEY_SCREENSHOT = 1;
    public const int HOTKEY_SAVE_VIDEO = 2;
    public const int HOTKEY_TOGGLE_OVERLAY = 3;

    private readonly IntPtr _handle;

    public HotkeyManager(IntPtr windowHandle)
    {
        _handle = windowHandle;
    }

    public void RegisterAll(string screenshot, string saveVideo, string toggleUI)
    {
        UnregisterAll();
        Register(HOTKEY_SCREENSHOT, screenshot);
        Register(HOTKEY_SAVE_VIDEO, saveVideo);
        Register(HOTKEY_TOGGLE_OVERLAY, toggleUI);
    }

    private void Register(int id, string hotkey)
    {
        var (mods, vk) = Parse(hotkey);
        RegisterHotKey(_handle, id, mods, vk);
    }

    public void UnregisterAll()
    {
        UnregisterHotKey(_handle, HOTKEY_SCREENSHOT);
        UnregisterHotKey(_handle, HOTKEY_SAVE_VIDEO);
        UnregisterHotKey(_handle, HOTKEY_TOGGLE_OVERLAY);
    }

    // Парсить рядок типу "Alt+F1" у модифікатори і код клавіші
    public static (uint mods, uint vk) Parse(string hotkey)
    {
        uint mods = 0;
        uint vk = 0;
        var parts = hotkey.Split('+');
        foreach (var part in parts)
        {
            switch (part.Trim().ToLower())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl": mods |= MOD_CTRL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (Enum.TryParse<Keys>(part.Trim(), true, out var key))
                        vk = (uint)key;
                    break;
            }
        }
        return (mods, vk);
    }

    public void Dispose() => UnregisterAll();
}