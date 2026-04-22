using System.Runtime.InteropServices;

namespace EventCapture.App;

public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint VK_F1 = 0x70;
    private const uint VK_F2 = 0x71;
    private const uint VK_F3 = 0x72;

    public const int HOTKEY_SCREENSHOT = 1;
    public const int HOTKEY_SAVE_VIDEO = 2;
    public const int HOTKEY_TOGGLE_OVERLAY = 3;

    private readonly IntPtr _handle;

    public HotkeyManager(IntPtr windowHandle)
    {
        _handle = windowHandle;
        RegisterHotKey(_handle, HOTKEY_SCREENSHOT, MOD_ALT, VK_F1);
        RegisterHotKey(_handle, HOTKEY_SAVE_VIDEO, MOD_ALT, VK_F2);
        RegisterHotKey(_handle, HOTKEY_TOGGLE_OVERLAY, MOD_ALT, VK_F3);
    }

    public void Dispose()
    {
        UnregisterHotKey(_handle, HOTKEY_SCREENSHOT);
        UnregisterHotKey(_handle, HOTKEY_SAVE_VIDEO);
        UnregisterHotKey(_handle, HOTKEY_TOGGLE_OVERLAY);
    }
}