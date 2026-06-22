using System.Windows;
using System.IO;
using EventCapture.App.Services;
using EventCapture.App.ViewModels;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private CaptureCoordinator? _capture;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private OverlayWindow? _overlay;
    private OverlayViewModel? _overlayViewModel;
    private bool _mediaFoundationStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceMutex = new Mutex(true, "EventCapture_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show("Skadi is already running.", "Skadi");
            Shutdown();
            return;
        }

        try
        {
            SharpDX.MediaFoundation.MediaFactory.Startup(
                SharpDX.MediaFoundation.MediaFactory.Version, 0);
            _mediaFoundationStarted = true;

            string baseDirectory = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDirectory, "ffmpeg.exe")))
            {
                FFMpegCore.GlobalFFOptions.Configure(new FFMpegCore.FFOptions
                {
                    BinaryFolder = baseDirectory
                });
            }

            var settings = AppSettings.Load();
            _window = new MainWindow();
            _ = new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
            _capture = new CaptureCoordinator();
            _hotkeys = new GlobalHotkeyService(_window);
            var notifications = new NotificationService();
            _overlay = new OverlayWindow();
            _overlayViewModel = new OverlayViewModel(_capture);
            _overlay.DataContext = _overlayViewModel;
            _tray = new TrayIconService(Path.Combine(baseDirectory, "EventCapture.ico"));

            _viewModel = new MainViewModel(
                settings, _capture, new CaptureTargetService(), _hotkeys,
                notifications, _overlay, ToggleUiAsync, Shutdown,
                (screenshot, record) => _tray.UpdateHotkeys(screenshot, record));
            _window.DataContext = _viewModel;
            _window.PrepareHidden();

            _hotkeys.HotkeyPressed += HandleHotkey;
            _tray.ToggleUiRequested += () => Dispatcher.InvokeAsync(ShowUiAsync);
            _tray.ExitRequested += () => Dispatcher.Invoke(Shutdown);

            _ = _viewModel.InitializeAsync();
            AppLogger.Info("Skadi started in background mode.");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(App), ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Skadi startup failed");
            Shutdown();
        }
    }

    private void HandleHotkey(int id)
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel is null) return;
            switch (id)
            {
                case GlobalHotkeyService.ScreenshotId: _viewModel.ExecuteScreenshot(); break;
                case GlobalHotkeyService.SaveRecordId: _viewModel.ExecuteRecord(); break;
                case GlobalHotkeyService.ToggleUiId: _ = ToggleUiAsync(); break;
            }
        });
    }
    private async Task ShowUiAsync()
    {
        if (_window is null || _window.IsPanelVisible)
            return;

        await _window.ShowPanelAsync();
    }

    private async Task ToggleUiAsync()
    {
        if (_window is null) return;
        if (_window.IsPanelVisible) await _window.HidePanelAsync();
        else await _window.ShowPanelAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _viewModel?.Dispose(); } catch { }
        try { _overlayViewModel?.Dispose(); } catch { }
        try { _overlay?.Close(); } catch { }
        try { _hotkeys?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _capture?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        if (_mediaFoundationStarted)
        {
            try { SharpDX.MediaFoundation.MediaFactory.Shutdown(); } catch { }
        }
        _singleInstanceMutex?.Dispose();
        AppLogger.Info("Skadi stopped.");
        base.OnExit(e);
    }
}
