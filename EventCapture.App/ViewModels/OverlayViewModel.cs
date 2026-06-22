using System.Windows.Threading;
using EventCapture.App.Infrastructure;
using EventCapture.App.Services;
using EventCapture.Core.Diagnostics;
using EventCapture.Core.Monitoring;

namespace EventCapture.App.ViewModels;

public sealed class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly CaptureCoordinator _capture;
    private readonly DispatcherTimer _timer;
    private HardwareMonitor? _hardware;
    private long _lastFrames;
    private string _cpu = "CPU --%";
    private string _gpu = "GPU --%";
    private string _ram = "RAM -- / -- GB";
    private string _fps = "FPS --";
    private bool _showFps;

    public OverlayViewModel(CaptureCoordinator capture)
    {
        _capture = capture;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Update(), Dispatcher.CurrentDispatcher);
        _timer.Start();
        _ = InitializeHardwareAsync();
    }

    public string Cpu { get => _cpu; private set => SetProperty(ref _cpu, value); }
    public string Gpu { get => _gpu; private set => SetProperty(ref _gpu, value); }
    public string Ram { get => _ram; private set => SetProperty(ref _ram, value); }
    public string Fps { get => _fps; private set => SetProperty(ref _fps, value); }
    public bool ShowFps { get => _showFps; private set => SetProperty(ref _showFps, value); }

    private async Task InitializeHardwareAsync()
    {
        try { _hardware = await Task.Run(() => new HardwareMonitor()); }
        catch (Exception ex) { AppLogger.Error(nameof(OverlayViewModel), ex.ToString()); }
    }

    private void Update()
    {
        try
        {
            _hardware?.Update();
            if (_hardware is not null)
            {
                Cpu = $"CPU {_hardware.CpuLoad:0}% · {_hardware.CpuFrequency:0} MHz";
                Gpu = $"GPU {_hardware.GpuLoad:0}% · VRAM {_hardware.GpuVram:0.0} GB";
                Ram = $"RAM {_hardware.RamUsed:0.0} / {_hardware.TotalRamGB:0.0} GB";
            }

            ShowFps = _capture.IsCapturingWindow;
            long frames = _capture.CapturedFrames;
            Fps = $"FPS {Math.Max(0, frames - _lastFrames)}";
            _lastFrames = frames;
        }
        catch (Exception ex) { AppLogger.Debug($"Overlay update: {ex.Message}"); }
    }

    public void Dispose()
    {
        _timer.Stop();
        _hardware?.Dispose();
    }
}
