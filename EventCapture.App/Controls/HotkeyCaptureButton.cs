using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EventCapture.App.Controls;

public sealed class HotkeyCaptureButton : System.Windows.Controls.Button
{
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(
            nameof(Hotkey),
            typeof(string),
            typeof(HotkeyCaptureButton),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnHotkeyChanged));

    public static readonly DependencyProperty IsCapturingProperty =
        DependencyProperty.Register(
            nameof(IsCapturing),
            typeof(bool),
            typeof(HotkeyCaptureButton),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CaptureStartedCommandProperty =
        DependencyProperty.Register(
            nameof(CaptureStartedCommand),
            typeof(ICommand),
            typeof(HotkeyCaptureButton));

    public static readonly DependencyProperty CaptureCompletedCommandProperty =
        DependencyProperty.Register(
            nameof(CaptureCompletedCommand),
            typeof(ICommand),
            typeof(HotkeyCaptureButton));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingProperty, value);
    }

    public ICommand? CaptureStartedCommand
    {
        get => (ICommand?)GetValue(CaptureStartedCommandProperty);
        set => SetValue(CaptureStartedCommandProperty, value);
    }

    public ICommand? CaptureCompletedCommand
    {
        get => (ICommand?)GetValue(CaptureCompletedCommandProperty);
        set => SetValue(CaptureCompletedCommandProperty, value);
    }

    public HotkeyCaptureButton()
    {
        Focusable = true;
        Loaded += (_, _) => Content = DisplayHotkey(Hotkey);
    }

    protected override void OnClick()
    {
        base.OnClick();

        IsCapturing = true;
        Content = "Press keys...";

        if (CaptureStartedCommand?.CanExecute(null) == true)
            CaptureStartedCommand.Execute(null);

        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System
            ? e.SystemKey
            : e.Key;

        if (key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (key == Key.Back)
        {
            Hotkey = string.Empty;
            IsCapturing = false;
            Content = DisplayHotkey(Hotkey);

            if (CaptureCompletedCommand?.CanExecute(null) == true)
                CaptureCompletedCommand.Execute(null);

            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");

        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");

        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");

        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");

        parts.Add(FormatKey(key));

        Hotkey = string.Join("+", parts);
        IsCapturing = false;
        Content = DisplayHotkey(Hotkey);

        if (CaptureCompletedCommand?.CanExecute(null) == true)
            CaptureCompletedCommand.Execute(null);

        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);

        if (IsCapturing)
            CancelCapture();
    }

    private void CancelCapture()
    {
        IsCapturing = false;
        Content = DisplayHotkey(Hotkey);

        if (CaptureCompletedCommand?.CanExecute(null) == true)
            CaptureCompletedCommand.Execute(null);
    }

    private static string DisplayHotkey(string? hotkey) =>
        string.IsNullOrWhiteSpace(hotkey)
            ? "Not assigned"
            : hotkey;

    private static string FormatKey(Key key)
    {
        var name = key.ToString();

        if (name.Length == 2 &&
            name[0] == 'D' &&
            char.IsDigit(name[1]))
        {
            return name[1].ToString();
        }

        return name switch
        {
            "OemPlus" => "+",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            _ => name
        };
    }

    private static void OnHotkeyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var button = (HotkeyCaptureButton)dependencyObject;

        if (!button.IsCapturing)
            button.Content = DisplayHotkey(e.NewValue?.ToString());
    }
}