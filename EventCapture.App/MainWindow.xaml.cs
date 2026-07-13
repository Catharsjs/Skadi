using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using EventCapture.App.Services;

namespace EventCapture.App;

public partial class MainWindow : Window
{
    private const int FadeDurationMs = 220;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    private readonly MonitorPanelSizer _panelSizer;
    private readonly SemaphoreSlim _fadeLock = new(1, 1);
    private IntPtr _windowHandle;
    private bool _panelVisible;
    private bool _prepared;

    public MainWindow()
    {
        InitializeComponent();
        _panelSizer = new MonitorPanelSizer(this);

        SourceInitialized += (_, _) =>
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _panelSizer.Attach();
        };

        Closed += (_, _) => _panelSizer.Detach();
    }

    public bool IsPanelVisible => _panelVisible;

    public void PrepareHidden()
    {
        if (_prepared)
            return;

        _prepared = true;
        Opacity = 1;
        RootVisual.BeginAnimation(UIElement.OpacityProperty, null);
        RootVisual.Opacity = 0;
        IsHitTestVisible = false;
        Show();
        SetClickThrough(true);
    }

    public async Task ShowPanelAsync()
    {
        await _fadeLock.WaitAsync();

        try
        {
            if (_panelVisible)
                return;

            _panelVisible = true;
            SetClickThrough(false);
            IsHitTestVisible = true;
            Activate();
            await AnimateContentOpacityAsync(1);
        }
        finally
        {
            _fadeLock.Release();
        }
    }

    public async Task HidePanelAsync()
    {
        await _fadeLock.WaitAsync();

        try
        {
            if (!_panelVisible)
                return;

            _panelVisible = false;
            await AnimateContentOpacityAsync(0);
            IsHitTestVisible = false;
            SetClickThrough(true);
        }
        finally
        {
            _fadeLock.Release();
        }
    }

    private Task AnimateContentOpacityAsync(double targetOpacity)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var animation = new DoubleAnimation
        {
            From = RootVisual.Opacity,
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.HoldEnd
        };

        animation.Completed += OnCompleted;

        RootVisual.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);

        return completion.Task;

        void OnCompleted(object? sender, EventArgs eventArgs)
        {
            animation.Completed -= OnCompleted;
            RootVisual.BeginAnimation(UIElement.OpacityProperty, null);
            RootVisual.Opacity = targetOpacity;
            completion.TrySetResult();
        }
    }

    private void SetClickThrough(bool enabled)
    {
        EnsureWindowHandle();

        nint currentStyle = GetWindowLongPtr(_windowHandle, GwlExStyle);
        long styleValue = currentStyle.ToInt64();
        styleValue |= WsExToolWindow;
        styleValue &= ~WsExAppWindow;
        styleValue = enabled
            ? styleValue | WsExTransparent
            : styleValue & ~WsExTransparent;

        SetWindowLongPtr(
            _windowHandle,
            GwlExStyle,
            new nint(styleValue));

        SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize |
            SwpNoMove |
            SwpNoZOrder |
            SwpNoActivate |
            SwpFrameChanged);
    }

    private void EnsureWindowHandle()
    {
        if (_windowHandle != IntPtr.Zero)
            return;

        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        IntPtr windowHandle,
        int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
