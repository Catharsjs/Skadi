using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly HwndSource _source;
    private readonly ContextMenu _menu;
    private readonly MenuItem _autoStartItem;
    private readonly DispatcherTimer _retryTimer;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private bool _added;
    private bool _disposed;

    public event Action? ToggleUiRequested;
    public event Action? ExitRequested;

    public TrayIconService(string iconPath)
    {
        _source = new HwndSource(new HwndSourceParameters("Skadi.Tray")
        {
            Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = 0x80
        });
        _source.AddHook(WndProc);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle,
            Id = 1, Flags = 1 | 2 | 4, Callback = CallbackMessage,
            Icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40),
            Tip = "Skadi", Info = string.Empty, InfoTitle = string.Empty
        };
        if (_data.Icon == IntPtr.Zero)
        {
            _source.Dispose();
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        _menu = new ContextMenu
        {
            Background = GetBrush("BackgroundBrush"), Foreground = GetBrush("PrimaryTextBrush"),
            BorderBrush = GetBrush("BorderBrush"), BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            Placement = PlacementMode.MousePoint
        };
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, GetBrush("BackgroundBrush")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, GetBrush("PrimaryTextBrush")));
        var template = new ControlTemplate(typeof(MenuItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 7, 20, 7));
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        panel.AppendChild(label);
        var check = new FrameworkElementFactory(typeof(TextBlock));
        check.Name = "Check";
        check.SetValue(TextBlock.TextProperty, "\u2713");
        check.SetValue(TextBlock.ForegroundProperty, GetBrush("AccentBrush"));
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        check.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        panel.AppendChild(check);
        border.AppendChild(panel);
        template.VisualTree = border;
        var checkedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Check"));
        template.Triggers.Add(checkedTrigger);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, GetBrush("HoverBrush")));
        style.Triggers.Add(hover);
        _menu.Resources.Add(typeof(MenuItem), style);
        var open = new MenuItem { Header = "Open Skadi" };
        open.Click += (_, _) => ToggleUiRequested?.Invoke();
        _autoStartItem = new MenuItem { Header = "Launch at Startup", StaysOpenOnClick = true };
        _autoStartItem.Click += (_, _) =>
        {
            try
            {
                AppSettings.SetAutoStart(!AppSettings.IsAutoStartEnabled());
                _autoStartItem.IsChecked = AppSettings.IsAutoStartEnabled();
            }
            catch (Exception ex) { AppLogger.Error(nameof(TrayIconService), ex.ToString()); }
        };
        var exit = new MenuItem { Header = "Exit", Foreground = GetBrush("DangerBrush") };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(open);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retryTimer.Tick += (_, _) => AddIcon();
        AddIcon();
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.FindResource(key);

    private void AddIcon()
    {
        if (_disposed) return;
        _added = Shell_NotifyIcon(0, ref _data);
        if (_added) _retryTimer.Stop();
        else _retryTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) AddIcon();
        if (message == CallbackMessage)
        {
            handled = true;
            if (lParam.ToInt32() == 0x203) ToggleUiRequested?.Invoke();
            else if (lParam.ToInt32() is 0x205 or 0x7B)
            {
                _autoStartItem.IsChecked = AppSettings.IsAutoStartEnabled();
                SetForegroundWindow(hwnd);
                _menu.IsOpen = true;
            }
        }
        return IntPtr.Zero;
    }

    public void UpdateHotkeys(string screenshot, string record)
    {
        string tip = $"Skadi\nScreenshot: {screenshot}\nReplay: {record}";
        _data.Tip = tip.Length > 127 ? tip[..127] : tip;
        if (_added && !_disposed) Shell_NotifyIcon(1, ref _data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        _menu.IsOpen = false;
        if (_added) Shell_NotifyIcon(2, ref _data);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        DestroyIcon(_data.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
