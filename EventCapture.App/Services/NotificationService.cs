using System.Windows;

namespace EventCapture.App.Services;

public sealed class NotificationService
{
    private NotificationWindow? _activeWindow;

    public void Show(string message) =>
        ShowCore(message, persistent: false);

    public void ShowProgress(string message) =>
        ShowCore(message, persistent: true);

    public void Dismiss()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(CloseActiveWindow);
    }

    private void ShowCore(string message, bool persistent)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            CloseActiveWindow();
            var window = new NotificationWindow(message, persistent);
            _activeWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_activeWindow, window))
                    _activeWindow = null;
            };
            window.Show();
        });
    }

    private void CloseActiveWindow()
    {
        NotificationWindow? window = _activeWindow;
        _activeWindow = null;
        if (window is null)
            return;

        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
