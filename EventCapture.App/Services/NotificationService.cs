using System.Windows;

namespace EventCapture.App.Services;

public sealed class NotificationService
{
    public void Show(string message)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() => new NotificationWindow(message).Show());
    }
}
