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
        SourceInitialized += (_, _) => SetWindowDisplayAffinity(
            new WindowInteropHelper(this).Handle, 0x00000011);
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
