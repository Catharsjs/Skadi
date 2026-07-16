using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const int ScreenshotId = 1;
    public const int SaveRecordId = 2;
    public const int StartStopRecordId = 3;
    public const int ToggleUiId = 4;

    private const int WmHotkey = 0x0312;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action<int>? HotkeyPressed;

    public GlobalHotkeyService(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();

        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("WPF window source is unavailable.");

        _source.AddHook(WndProc);
    }

    // Реєстрація глобальних гарячих клавіш Windows ...
    public IReadOnlyList<int> RegisterAll(
        string screenshot,
        string record,
        string startStopRecord,
        string toggleUi)
    {
        UnregisterAll();

        List<int> rejected = [];

        if (!Register(ScreenshotId, screenshot))
            rejected.Add(ScreenshotId);
        if (!Register(SaveRecordId, record))
            rejected.Add(SaveRecordId);
        if (!Register(StartStopRecordId, startStopRecord))
            rejected.Add(StartStopRecordId);
        if (!Register(ToggleUiId, toggleUi))
            rejected.Add(ToggleUiId);

        _registered = true;
        return rejected;
    }
    // ...Реєстрація глобальних гарячих клавіш Windows

    // Скасування реєстрації глобальних гарячих клавіш ...
    public void UnregisterAll()
    {
        if (!_registered)
            return;

        UnregisterHotKey(_handle, ScreenshotId);
        UnregisterHotKey(_handle, SaveRecordId);
        UnregisterHotKey(_handle, StartStopRecordId);
        UnregisterHotKey(_handle, ToggleUiId);

        _registered = false;
    }
    // ...Скасування реєстрації глобальних гарячих клавіш

    private bool Register(int id, string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return true;

        try
        {
            var (modifiers, virtualKey) = Parse(hotkey);

            if (RegisterHotKey(_handle, id, modifiers, virtualKey))
                return true;

            int error = Marshal.GetLastWin32Error();
            AppLogger.Error(nameof(GlobalHotkeyService), $"Could not register hotkey: {hotkey} | Win32={error}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(GlobalHotkeyService), $"Invalid hotkey: {hotkey} | {ex.Message}");
            return false;
        }
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey)
        {
            handled = true;
            HotkeyPressed?.Invoke(wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    private static (uint Modifiers, uint VirtualKey) Parse(string hotkey)
    {
        uint modifiers = 0;
        Key key = Key.None;

        foreach (string rawPart in hotkey.Split(
                     '+',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= 0x0002;
                    break;

                case "ALT":
                    modifiers |= 0x0001;
                    break;

                case "SHIFT":
                    modifiers |= 0x0004;
                    break;

                case "WIN":
                case "WINDOWS":
                    modifiers |= 0x0008;
                    break;

                default:
                    if (rawPart.Length == 1 && char.IsDigit(rawPart[0]))
                    {
                        key = (Key)((int)Key.D0 + (rawPart[0] - '0'));
                    }
                    else if (!Enum.TryParse(rawPart, true, out key))
                    {
                        throw new FormatException($"Unsupported hotkey: {hotkey}");
                    }

                    break;
            }
        }

        if (key == Key.None)
            throw new FormatException($"Hotkey has no key: {hotkey}");

        return (
            modifiers | 0x4000u,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hwnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hwnd,
        int id);
}
