using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace EventCapture.App;

public partial class NotificationWindow : Window
{
    public NotificationWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HideFromAltTab(handle);
            SetWindowDisplayAffinity(handle, 0x00000011);
        };
        Loaded += async (_, _) =>
        {
            Left =
                SystemParameters.WorkArea.Left +
                (SystemParameters.WorkArea.Width - ActualWidth) / 2;

            Top =
                SystemParameters.WorkArea.Top + 12;

            await FadeAsync(1);
            await Task.Delay(1800);
            await FadeAsync(0);

            Close();
        };
    }

    private Task FadeAsync(double target)
    {
        var completion = new TaskCompletionSource();
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        animation.Completed += (_, _) => completion.TrySetResult();
        BeginAnimation(OpacityProperty, animation);
        return completion.Task;
    }

    private static void HideFromAltTab(IntPtr handle)
    {
        nint styleValue = GetWindowLongPtr(handle, GwlExStyle);
        styleValue &= ~WsExAppWindow;
        styleValue |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, styleValue);
    }

    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExAppWindow = 0x00040000;
    private const nint WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    private static nint GetWindowLongPtr(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : GetWindowLong32(window, index);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    private static nint SetWindowLongPtr(IntPtr window, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : SetWindowLong32(window, index, value.ToInt32());
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
