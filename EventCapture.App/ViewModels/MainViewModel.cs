using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using EventCapture.App.Infrastructure;
using EventCapture.App.Models;
using EventCapture.App.Services;
using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using NAudio.CoreAudioApi;
namespace EventCapture.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int TransitionMilliseconds = 220;
    private readonly AppSettings _settings;
    private readonly CaptureCoordinator _capture;
    private readonly CaptureTargetService _targets;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly NotificationService _notifications;
    private readonly ScreenshotSelectionService _screenshotSelection = new();
    private readonly OverlayWindow _overlay;
    private readonly OverlayViewModel _overlayViewModel;
    private readonly Func<Task> _toggleUi;
    private readonly Action<string, string> _updateTrayHotkeys;
    private readonly Dictionary<string, string?> _systemDeviceIds = [];
    private readonly Dictionary<string, string?> _microphoneDeviceIds = [];
    private IReadOnlyList<CapturePreview> _monitors = [];
    private CancellationTokenSource? _settingsCts;
    private CancellationTokenSource? _eventCts;
    private CancellationTokenSource? _systemVolumeAnimationCts;
    private CancellationTokenSource? _microphoneVolumeAnimationCts;
    private CancellationTokenSource? _audioDeviceRefreshCts;
    private CancellationTokenSource? _targetRefreshCts;
    private CancellationTokenSource? _screenshotCaptureCts;
    private MMDeviceEnumerator? _audioDeviceEnumerator;
    private AudioDeviceNotificationClient? _audioDeviceNotificationClient;
    private DispatcherTimer? _audioLevelTimer;
    private DispatcherTimer? _recordingTimer;
    private readonly Stopwatch _recordingStopwatch = new();
    private long _recordingTimerLastTickTimestamp;
    private long _recordingTimerLastLogTimestamp;
    private long _recordingTimerTickCount;
    private MMDevice? _systemAudioMeterDevice;
    private MMDevice? _microphoneMeterDevice;
    private bool _restartPending;
    private bool _eventIsWarning;
    private bool _suppressAudioDeviceChangeEvents;
    private int _captureSectionTransitionVersion;
    private bool _initializing = true;
    private bool _bufferEnabled;
    private bool _isAdvanced;
    private string _captureMode;
    private CapturePreview? _selectedPreview;
    private string _selectedTargetValue;
    private int _previewPage;
    private bool _isVideoSectionActive = true;
    private bool _isAudioSectionActive = true;
    private bool _isVideoInputEnabled = true;
    private bool _isAudioInputEnabled = true;
    private string _resolution;
    private string _quality;
    private int _frameRate;
    private int _bufferDuration;
    private string _systemAudioDevice = string.Empty;
    private string _microphoneDevice = string.Empty;
    private double _systemVolume;
    private double _microphoneVolume;
    private double _systemAudioLevel;
    private double _microphoneLevel;
    private double _lastSystemVolume;
    private double _lastMicrophoneVolume;
    private bool _systemAudioEnabled;
    private bool _microphoneEnabled;
    private bool _isSystemAudioInputEnabled;
    private bool _isMicrophoneInputEnabled;
    private string _saveFolder;
    private string _hudMode;
    private string _screenshotHotkey;
    private string _recordHotkey;
    private string _startStopRecordHotkey;
    private string _toggleUiHotkey;
    private bool _isContinuousRecording;
    private bool _isRecordStateChanging;
    private DateTimeOffset _recordingStartedAt;
    private TimeSpan _recordingElapsed;
    private string _eventMessage = "Initializing capture...";
    private bool _isEventVisible = true;
    private double _targetPreviewOpacity = 1.0;

    public MainViewModel(
        AppSettings settings,
        CaptureCoordinator capture,
        CaptureTargetService targets,
        GlobalHotkeyService hotkeys,
        NotificationService notifications,
        OverlayWindow overlay,
        OverlayViewModel overlayViewModel,
        Func<Task> toggleUi,
        Action exit,
        Action<string, string> updateTrayHotkeys)
    {
        _settings = settings;
        _capture = capture;
        _targets = targets;
        _hotkeys = hotkeys;
        _notifications = notifications;
        _overlay = overlay;
        _overlayViewModel = overlayViewModel;
        _capture.ContinuousRecordingStopping += OnContinuousRecordingStopping;
        _capture.ContinuousRecordingStopped += OnContinuousRecordingStopped;
        _toggleUi = toggleUi;
        _ = exit;
        _updateTrayHotkeys = updateTrayHotkeys;

        _bufferEnabled = settings.BufferEnabled;
        _captureMode = FromStoredMode(settings.CaptureMode);
        _selectedTargetValue = settings.CaptureTarget;
        _resolution = FromStoredResolution(settings.Resolution);
        _quality = settings.VideoQuality switch { <= 50 => "Low", <= 70 => "Medium", _ => "High" };
        _frameRate = NormalizeFrameRate(settings.Fps);
        _bufferDuration = settings.BufferSeconds;
        _systemAudioEnabled = settings.RecordSystemAudio;
        _microphoneEnabled = settings.RecordMicrophone;
        _isSystemAudioInputEnabled = _systemAudioEnabled;
        _isMicrophoneInputEnabled = _microphoneEnabled;
        _lastSystemVolume = settings.SystemAudioVolume;
        _lastMicrophoneVolume = settings.MicVolume;
        _systemVolume =
    _systemAudioEnabled
        ? _lastSystemVolume
        : 0;

        _microphoneVolume =
            _microphoneEnabled
                ? _lastMicrophoneVolume
                : 0;
        _saveFolder = settings.SaveFolder;
        _hudMode = settings.HudMode;
        _screenshotHotkey = settings.HotkeyScreenshot;
        _recordHotkey = settings.HotkeySaveVideo;
        _startStopRecordHotkey = settings.HotkeyStartStopRecord;
        _toggleUiHotkey = settings.HotkeyToggleUI;
        UpdateCaptureSectionState(_captureMode, immediate: true);

        OpenSaveFolderCommand = new RelayCommand(_ => OpenSaveFolder());
        SelectFolderCommand = new AsyncRelayCommand(SelectFolderAsync);
        SaveScreenshotCommand = new AsyncRelayCommand(
            SaveScreenshotAsync,
            commandName: "SaveScreenshot",
            uiLockTimeout: TimeSpan.FromSeconds(5),
            uiLockTimeoutAllowed: () => !_screenshotSelection.IsSelectionActive);
        SaveRecordCommand = new AsyncRelayCommand(SaveRecordAsync, commandName: "SaveReplay", uiLockTimeout: TimeSpan.FromSeconds(5));
        StartStopRecordCommand = new AsyncRelayCommand(
            ToggleContinuousRecordingAsync,
            commandName: "StartStopRecording",
            uiLockTimeout: TimeSpan.FromSeconds(5),
            uiLockTimeoutAllowed: () => !_isRecordStateChanging);
        ToggleBufferCommand = new RelayCommand(_ => BufferEnabled = !BufferEnabled);
        NextPreviewPageCommand = new RelayCommand(_ => NextPreviewPage());
        CycleVideoSettingCommand = new RelayCommand(parameter => CycleVideoSetting(parameter?.ToString()));
        HotkeyCaptureStartedCommand = new RelayCommand(_ => _hotkeys.UnregisterAll());
        HotkeyCaptureCompletedCommand = new RelayCommand(_ => ApplyHotkeys());
        ExitCommand = new AsyncRelayCommand(_toggleUi);
        SettingsLockedCommand = new RelayCommand(_ => ShowSettingsLockedMessage());
    }

    public ObservableCollection<CapturePreview> VisiblePreviews { get; } = [];
    public ObservableCollection<string> SystemAudioDevices { get; } = [];
    public ObservableCollection<string> MicrophoneDevices { get; } = [];
    public string NativeResolutionLabel
    {
        get
        {
            try
            {
                var (width, height) = ScreenCapturer.GetTargetSize(_selectedTargetValue);
                return $"Native ({width}x{height})";
            }
            catch
            {
                return "Native";
            }
        }
    }

    public string[] Resolutions
    {
        get
        {
            var resolutions = new List<string>
        {
            "1280x720"
        };

            try
            {
                var (_, height) = ScreenCapturer.GetTargetSize(_selectedTargetValue);

                if (height >= 1440)
                    resolutions.Add("1920x1080");

                if (height >= 2160)
                    resolutions.Add("2560x1440");
            }
            catch
            {
                resolutions.Add("1920x1080");
            }

            resolutions.Add(NativeResolutionLabel);

            return resolutions.ToArray();
        }
    }
    public int[] FrameRates { get; } = [30, 60];
    public int[] BufferDurations { get; } = [15, 30, 60, 120];

    public ICommand OpenSaveFolderCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand SaveScreenshotCommand { get; }
    public ICommand SaveRecordCommand { get; }
    public ICommand StartStopRecordCommand { get; }
    public ICommand ToggleBufferCommand { get; }
    public ICommand NextPreviewPageCommand { get; }
    public ICommand CycleVideoSettingCommand { get; }
    public ICommand HotkeyCaptureStartedCommand { get; }
    public ICommand HotkeyCaptureCompletedCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand SettingsLockedCommand { get; }

    // Ініціалізація головної моделі та підсистем захоплення ...
    public async Task InitializeAsync()
    {
        StartDisplayChangeMonitoring();
        StartAudioDeviceMonitoring();
        LoadAudioDevices();
        StartAudioLevelMonitoring();
        await RefreshTargetsAsync();

        _previewPage = 0;
        RefreshVisiblePreviews();
        _initializing = false;
        ApplyHotkeys();
        ApplyHudMode();
        try
        {
            ApplyToSettings();
            await _capture.ApplySettingsAsync(_settings, restartPipeline: true);
            LogEvent(_bufferEnabled ? "Replay buffer started" : "Replay buffer disabled");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            LogEvent("Capture initialization failed");
            _notifications.Show("Capture initialization failed");
        }
    }
    // ...Ініціалізація головної моделі та підсистем захоплення

    public bool BufferEnabled
    {
        get => _bufferEnabled;

        set
        {
            if (!SetProperty(ref _bufferEnabled, value))
            {
                return;
            }

            AppLogger.Info($"UI state | Action=BufferToggle changed | Buffer={value} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | CanEdit={CanEditSettings} | Frames={_capture.CapturedFrames}");

            OnPropertyChanged(nameof(BufferStatus));
            OnPropertyChanged(nameof(BufferToggleText));
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(IsSettingsLocked));

            UpdateCaptureSectionState(
                _captureMode,
                immediate: false);

            LogEvent(
                value
                    ? "Buffer enabled"
                    : "Buffer disabled");

            _settingsCts?.Cancel();
            _restartPending = false;
            _ = ApplyBufferStateImmediatelyAsync();
        }
    }
    public string BufferStatus => BufferEnabled ? "Buffer Enabled" : "Buffer Disabled";
    public string BufferToggleText => BufferEnabled ? "Disable Buffer" : "Enable Buffer";
    public bool IsAdvanced { get => _isAdvanced; set { if (SetProperty(ref _isAdvanced, value)) LogEvent(value ? "Advanced settings" : "Basic settings"); } }

    public string CaptureMode
    {
        get => _captureMode;
        set
        {
            if (!SetProperty(ref _captureMode, value)) return;
            UpdateCaptureSectionState(value, immediate: false);
            LogEvent($"Capture mode: {value}");
            QueueSettingsUpdate(true);
        }
    }

    public CapturePreview? SelectedPreview
    {
        get => _selectedPreview;

        set
        {
            if (!SetProperty(
                    ref _selectedPreview,
                    value))
            {
                return;
            }

            if (value is null)
                return;

            _selectedTargetValue =
                value.TargetValue;

            RefreshNativeResolutionLabel();

            LogEvent(
                $"Selected: {value.Title}");

            QueueSettingsUpdate(true);
        }
    }

    public bool IsVideoSectionActive { get => _isVideoSectionActive; private set => SetProperty(ref _isVideoSectionActive, value); }
    public bool IsAudioSectionActive { get => _isAudioSectionActive; private set => SetProperty(ref _isAudioSectionActive, value); }
    public bool IsVideoInputEnabled { get => _isVideoInputEnabled; private set => SetProperty(ref _isVideoInputEnabled, value); }
    public bool IsAudioInputEnabled { get => _isAudioInputEnabled; private set => SetProperty(ref _isAudioInputEnabled, value); }
    public bool HasNextPreviewPage => CurrentTargets.Count > 4;
    public int PreviewGridColumns => CurrentTargets.Count <= 1 ? 1 : 2;
    public int PreviewGridRows => CurrentTargets.Count <= 1 ? 1 : 2;
    public string PreviewPageLabel => $"{_previewPage + 1} / {Math.Max(1, (int)Math.Ceiling(CurrentTargets.Count / 4d))}";

    public string Resolution { get => _resolution; set { if (SetProperty(ref _resolution, value)) { LogEvent($"Resolution: {value}"); QueueSettingsUpdate(true); } } }
    public string Quality { get => _quality; set { if (SetProperty(ref _quality, value)) { OnPropertyChanged(nameof(QualityBitrate)); LogEvent($"Quality: {value} ({QualityBitrate})"); QueueSettingsUpdate(true); } } }
    public string QualityBitrate => Quality switch { "Low" => "50% bitrate", "High" => "90% bitrate", _ => "70% bitrate" };
    public int FrameRate { get => _frameRate; set { if (SetProperty(ref _frameRate, value)) { LogEvent($"Frame rate: {value} FPS"); QueueSettingsUpdate(true); } } }
    public int BufferDuration { get => _bufferDuration; set { if (SetProperty(ref _bufferDuration, value)) { LogEvent($"Buffer duration: {value} sec"); QueueSettingsUpdate(false); } } }
    public string SystemAudioDevice { get => _systemAudioDevice; set { if (SetProperty(ref _systemAudioDevice, value)) { RefreshAudioMeterDevices(); if (!_suppressAudioDeviceChangeEvents) { LogEvent("System audio device changed"); QueueSettingsUpdate(true); } } } }
    public string MicrophoneDevice { get => _microphoneDevice; set { if (SetProperty(ref _microphoneDevice, value)) { RefreshAudioMeterDevices(); if (!_suppressAudioDeviceChangeEvents) { LogEvent("Microphone device changed"); QueueSettingsUpdate(true); } } } }

    public double SystemVolume
    {
        get => _systemVolume;
        set { if (SetProperty(ref _systemVolume, value)) { if (SystemAudioEnabled) _lastSystemVolume = value; QueueSettingsUpdate(false); } }
    }
    public double MicrophoneVolume
    {
        get => _microphoneVolume;
        set { if (SetProperty(ref _microphoneVolume, value)) { if (MicrophoneEnabled) _lastMicrophoneVolume = value; QueueSettingsUpdate(false); } }
    }
    public double SystemAudioLevel { get => _systemAudioLevel; private set => SetProperty(ref _systemAudioLevel, value); }
    public double MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }

    public bool SystemAudioEnabled
    {
        get => _systemAudioEnabled;
        set
        {
            if (!SetProperty(ref _systemAudioEnabled, value)) return;
            if (value) IsSystemAudioInputEnabled = true;
            _ = AnimateVolumeAsync(true, value ? _lastSystemVolume : 0);
            _ = UpdateDeviceInputAsync(true, value);
            LogEvent(value ? "System audio enabled" : "System audio disabled");
            QueueSettingsUpdate(true);
        }
    }
    public bool MicrophoneEnabled
    {
        get => _microphoneEnabled;
        set
        {
            if (!SetProperty(ref _microphoneEnabled, value)) return;
            if (value) IsMicrophoneInputEnabled = true;
            _ = AnimateVolumeAsync(false, value ? _lastMicrophoneVolume : 0);
            _ = UpdateDeviceInputAsync(false, value);
            LogEvent(value ? "Microphone enabled" : "Microphone disabled");
            QueueSettingsUpdate(true);
        }
    }

    public bool IsSystemAudioInputEnabled { get => _isSystemAudioInputEnabled; private set => SetProperty(ref _isSystemAudioInputEnabled, value); }
    public bool IsMicrophoneInputEnabled { get => _isMicrophoneInputEnabled; private set => SetProperty(ref _isMicrophoneInputEnabled, value); }
    public string SaveFolder { get => _saveFolder; set { if (SetProperty(ref _saveFolder, value)) QueueSettingsUpdate(false); } }
    public string HudMode
    {
        get => _hudMode;
        set
        {
            string normalizedMode = value is "Timer" or "System Info" ? value : "None";
            if (!SetProperty(ref _hudMode, normalizedMode)) return;
            ApplyHudMode();
            LogEvent($"HUD: {normalizedMode}");
            QueueSettingsUpdate(false);
        }
    }
    public string ScreenshotHotkey { get => _screenshotHotkey; set { if (SetProperty(ref _screenshotHotkey, value)) { LogEvent($"Screenshot hotkey: {value}"); QueueSettingsUpdate(false); } } }
    public string RecordHotkey { get => _recordHotkey; set { if (SetProperty(ref _recordHotkey, value)) { LogEvent($"Replay hotkey: {value}"); QueueSettingsUpdate(false); } } }
    public string ToggleUiHotkey { get => _toggleUiHotkey; set { if (SetProperty(ref _toggleUiHotkey, value)) { LogEvent($"Toggle UI hotkey: {value}"); QueueSettingsUpdate(false); } } }
    public string StartStopRecordHotkey { get => _startStopRecordHotkey; set { if (SetProperty(ref _startStopRecordHotkey, value)) { LogEvent($"Recording hotkey: {value}"); QueueSettingsUpdate(false); } } }
    public bool IsContinuousRecording
    {
        get => _isContinuousRecording;
        private set
        {
            if (!SetProperty(ref _isContinuousRecording, value))
                return;

            if (value)
                StartRecordingTimer();
            else
                StopRecordingTimer();

            OnPropertyChanged(nameof(StartStopRecordText));
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(IsSettingsLocked));
            UpdateCaptureSectionState(_captureMode, immediate: false);
        }
    }
    public string StartStopRecordText =>
        IsContinuousRecording
            ? $"Stop Recording - {_recordingElapsed:hh\\:mm\\:ss}"
            : "Start Recording";
    public string EventMessage { get => _eventMessage; private set => SetProperty(ref _eventMessage, value); }
    public bool IsEventVisible { get => _isEventVisible; private set => SetProperty(ref _isEventVisible, value); }
    public double TargetPreviewOpacity { get => _targetPreviewOpacity; private set => SetProperty(ref _targetPreviewOpacity, value); }
    public bool CanEditSettings => !BufferEnabled && !IsContinuousRecording;
    public bool IsSettingsLocked => !CanEditSettings;

    public bool EventIsWarning
    {
        get => _eventIsWarning;
        private set => SetProperty(ref _eventIsWarning, value);
    }

    private IReadOnlyList<CapturePreview> CurrentTargets => _monitors;

    private async Task RefreshTargetsAsync()
    {
        try
        {
            _monitors = await _targets.GetMonitorsAsync();
            _previewPage = 0;
            RefreshVisiblePreviews(selectStoredTarget: true, preserveMissingSelection: !CanEditSettings);
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            LogEvent("Could not enumerate capture targets");
        }
    }

    private void RefreshVisiblePreviews(
    bool selectStoredTarget = false,
    bool preserveMissingSelection = false)
    {
        VisiblePreviews.Clear();

        foreach (var item in CurrentTargets
                     .Skip(_previewPage * 4)
                     .Take(4))
        {
            VisiblePreviews.Add(item);
        }

        CapturePreview? selection =
            CurrentTargets.FirstOrDefault(
                item =>
                    item.TargetValue ==
                    _selectedTargetValue);

        if (selection is null && !preserveMissingSelection && CurrentTargets.Count > 0)
        {
            selection = CurrentTargets[0];
            _selectedTargetValue = selection.TargetValue;
        }

        _selectedPreview =
            selection;

        RefreshNativeResolutionLabel();

        OnPropertyChanged(
            nameof(SelectedPreview));

        OnPropertyChanged(
            nameof(HasNextPreviewPage));

        OnPropertyChanged(
            nameof(PreviewPageLabel));

        OnPropertyChanged(
            nameof(PreviewGridColumns));

        OnPropertyChanged(
            nameof(PreviewGridRows));
    }

    private void RefreshNativeResolutionLabel()
    {
        bool usesNative =
            Resolution.StartsWith(
                "Native",
                StringComparison.Ordinal);

        OnPropertyChanged(
            nameof(NativeResolutionLabel));

        OnPropertyChanged(
            nameof(Resolutions));

        if (!usesNative)
            return;

        _resolution =
            NativeResolutionLabel;

        OnPropertyChanged(
            nameof(Resolution));
    }

    private void NextPreviewPage()
    {
        int pages = Math.Max(1, (int)Math.Ceiling(CurrentTargets.Count / 4d));
        _previewPage = (_previewPage + 1) % pages;
        RefreshVisiblePreviews();
        LogEvent($"Preview page: {_previewPage + 1}");
    }

    private void StartDisplayChangeMonitoring()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void StopDisplayChangeMonitoring()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _targetRefreshCts?.Cancel();
        _targetRefreshCts?.Dispose();
        _targetRefreshCts = null;
        CancelActiveScreenshotSelection("Display monitoring stopped");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        CancelActiveScreenshotSelection("Display settings changed");
        _screenshotSelection.InvalidateOverlayWindows();
        ScreenCapturer.ReleaseScreenshotCaptureResources();
        QueueTargetRefresh("Display settings changed");
    }
    private void CancelActiveScreenshotSelection(string reason)
    {
        CancellationTokenSource? cts = _screenshotCaptureCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        AppLogger.Info($"Screenshot command canceled | Reason={reason}");
        cts.Cancel();
    }

    private void QueueTargetRefresh(string reason)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        dispatcher.BeginInvoke(() =>
        {
            _targetRefreshCts?.Cancel();
            _targetRefreshCts?.Dispose();
            _targetRefreshCts = new CancellationTokenSource();
            _ = RefreshTargetsAfterDisplayChangeAsync(reason, _targetRefreshCts.Token);
        });
    }

    private async Task RefreshTargetsAfterDisplayChangeAsync(string reason, CancellationToken cancellationToken)
    {
        bool recordingStateTransition = false;
        try
        {
            await Task.Delay(450, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            AppLogger.Info($"Display topology changed | Reason={reason} | Recording={IsContinuousRecording} | Buffer={BufferEnabled} | CanEdit={CanEditSettings} | Target={_selectedTargetValue}");

            bool recordingWasActive = IsContinuousRecording || _capture.IsContinuousRecording;
            bool bufferShouldRestart = BufferEnabled;
            if (recordingWasActive)
            {
                recordingStateTransition = true;
                _isRecordStateChanging = true;
                IsContinuousRecording = false;
                AppLogger.Info(
                    $"Reload (Targets updated) | UI recording state stopped for topology change | " +
                    $"Buffer={BufferEnabled} | CaptureRecording={_capture.IsContinuousRecording} | " +
                    $"Frames={_capture.CapturedFrames}");
            }

            string? finalizedPath = await _capture.HandleDisplayTopologyChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();

            await AnimateTargetPreviewOpacityAsync(0.0, cancellationToken);
            IReadOnlyList<CapturePreview> nextTargets = await _targets.GetMonitorsAsync();
            cancellationToken.ThrowIfCancellationRequested();

            _monitors = nextTargets;
            _previewPage = 0;
            RefreshVisiblePreviews(selectStoredTarget: true, preserveMissingSelection: false);
            await AnimateTargetPreviewOpacityAsync(1.0, cancellationToken);

            ApplyToSettings();
            await _capture.ApplySettingsAsync(_settings, restartPipeline: bufferShouldRestart);
            _settings.Save();
            AppLogger.Info(
                $"Reload (Targets updated) | Applied | Target={_settings.CaptureTarget} | " +
                $"RecordingFinalized={finalizedPath is not null} | " +
                $"FinalizedPath={Path.GetFileName(finalizedPath ?? string.Empty)} | " +
                $"BufferRestarted={bufferShouldRestart}");
            LogEvent("Reload (Targets updated)", warning: recordingWasActive);
            _notifications.Show("Reload (Targets updated)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), $"Target reload failed after display change: {ex}");
            TargetPreviewOpacity = 1.0;
            LogEvent("Reload failed", warning: true);
            _notifications.Show("Reload failed");
        }
        finally
        {
            if (recordingStateTransition)
            {
                _isRecordStateChanging = false;
                IsContinuousRecording = false;
                AppLogger.Info(
                    $"Reload (Targets updated) | UI recording state finalized | " +
                    $"Buffer={BufferEnabled} | CaptureRecording={_capture.IsContinuousRecording} | " +
                    $"Frames={_capture.CapturedFrames}");
            }
        }
    }

    private async Task AnimateTargetPreviewOpacityAsync(double target, CancellationToken cancellationToken)
    {
        double start = TargetPreviewOpacity;
        for (int frame = 1; frame <= 10; frame++)
        {
            await Task.Delay(TransitionMilliseconds / 10, cancellationToken);
            double progress = frame / 10d;
            double smooth = progress * progress * (3 - 2 * progress);
            TargetPreviewOpacity = start + ((target - start) * smooth);
        }
    }
    private void LoadAudioDevices()
    {
        bool previousSuppressAudioDeviceChangeEvents =
            _suppressAudioDeviceChangeEvents;

        _suppressAudioDeviceChangeEvents = true;

        try
        {
        bool hadSystemSelection =
            _systemDeviceIds.TryGetValue(
                _systemAudioDevice,
                out string? currentSystemId);

        bool hadMicrophoneSelection =
            _microphoneDeviceIds.TryGetValue(
                _microphoneDevice,
                out string? currentMicrophoneId);

        string? preferredSystemId =
            hadSystemSelection
                ? currentSystemId
                : _settings.SystemAudioDeviceId;

        string? preferredMicrophoneId =
            hadMicrophoneSelection
                ? currentMicrophoneId
                : _settings.MicDeviceId;

        bool preferDefaultSystem =
            preferredSystemId is null;

        bool preferDefaultMicrophone =
            preferredMicrophoneId is null;

        _systemDeviceIds.Clear();
        _microphoneDeviceIds.Clear();
        SystemAudioDevices.Clear();
        MicrophoneDevices.Clear();

        string defaultOutput =
            "Default system device";

        string defaultInput =
            "Default microphone";

        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            MMDevice defaultOutputDevice =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

            defaultOutput =
                $"Default: {defaultOutputDevice.FriendlyName}";
        }
        catch
        {
        }

        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            MMDevice defaultInputDevice =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Multimedia);

            defaultInput =
                $"Default: {defaultInputDevice.FriendlyName}";
        }
        catch
        {
        }

        AddDevice(
            SystemAudioDevices,
            _systemDeviceIds,
            defaultOutput,
            null);

        foreach (var device in AudioRecorder.GetOutputDevices())
        {
            AddDevice(
                SystemAudioDevices,
                _systemDeviceIds,
                device.Name,
                device.Id);
        }

        AddDevice(
            MicrophoneDevices,
            _microphoneDeviceIds,
            defaultInput,
            null);

        foreach (var device in AudioRecorder.GetInputDevices())
        {
            AddDevice(
                MicrophoneDevices,
                _microphoneDeviceIds,
                device.Name,
                device.Id);
        }

        _systemAudioDevice =
            preferDefaultSystem
                ? defaultOutput
                : FindDeviceName(
                    _systemDeviceIds,
                    preferredSystemId) ??
                  defaultOutput;

        _microphoneDevice =
            preferDefaultMicrophone
                ? defaultInput
                : FindDeviceName(
                    _microphoneDeviceIds,
                    preferredMicrophoneId) ??
                  defaultInput;

        OnPropertyChanged(
            nameof(SystemAudioDevice));

        OnPropertyChanged(
            nameof(MicrophoneDevice));

        RefreshAudioMeterDevices();
        }
        finally
        {
            _suppressAudioDeviceChangeEvents =
                previousSuppressAudioDeviceChangeEvents;
        }
    }

    private void StartAudioLevelMonitoring()
    {
        if (_audioLevelTimer is not null)
            return;

        RefreshAudioMeterDevices();

        _audioLevelTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };

        _audioLevelTimer.Tick += (_, _) => UpdateAudioLevels();
        _audioLevelTimer.Start();
    }

    private void RefreshAudioMeterDevices()
    {
        DisposeAudioMeterDevices();

        try
        {
            _audioDeviceEnumerator ??= new MMDeviceEnumerator();

            _systemAudioMeterDevice = ResolveAudioMeterDevice(
                DataFlow.Render,
                ResolveSelectedAudioDeviceId(_systemDeviceIds, SystemAudioDevice));

            _microphoneMeterDevice = ResolveAudioMeterDevice(
                DataFlow.Capture,
                ResolveSelectedAudioDeviceId(_microphoneDeviceIds, MicrophoneDevice));
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                $"Audio meter refresh failed: {ex}");
        }
    }

    private static string? ResolveSelectedAudioDeviceId(
        IReadOnlyDictionary<string, string?> deviceIds,
        string? selectedDeviceName)
    {
        return string.IsNullOrWhiteSpace(selectedDeviceName)
            ? null
            : deviceIds.TryGetValue(selectedDeviceName, out string? deviceId)
                ? deviceId
                : null;
    }

    private MMDevice? ResolveAudioMeterDevice(
        DataFlow flow,
        string? deviceId)
    {
        if (_audioDeviceEnumerator is null)
            return null;

        try
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? _audioDeviceEnumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : _audioDeviceEnumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateAudioLevels()
    {
        SystemAudioLevel = SmoothAudioLevel(
            SystemAudioLevel,
            ReadAudioPeak(_systemAudioMeterDevice));

        MicrophoneLevel = SmoothAudioLevel(
            MicrophoneLevel,
            ReadAudioPeak(_microphoneMeterDevice));
    }

    private static double ReadAudioPeak(
        MMDevice? device)
    {
        try
        {
            return Math.Clamp(
                (device?.AudioMeterInformation.MasterPeakValue ?? 0f) * 100.0,
                0,
                100);
        }
        catch
        {
            return 0;
        }
    }

    private static double SmoothAudioLevel(
        double current,
        double target)
    {
        if (target >= current)
            return target;

        return Math.Max(
            0,
            current * 0.72);
    }

    private void DisposeAudioMeterDevices()
    {
        try { _systemAudioMeterDevice?.Dispose(); } catch { }
        try { _microphoneMeterDevice?.Dispose(); } catch { }
        _systemAudioMeterDevice = null;
        _microphoneMeterDevice = null;
    }

    private void StartRecordingTimer()
    {
        _recordingStartedAt = DateTimeOffset.Now;
        _recordingStopwatch.Restart();
        _recordingTimerLastTickTimestamp = Stopwatch.GetTimestamp();
        _recordingTimerLastLogTimestamp = 0;
        _recordingTimerTickCount = 0;
        _recordingElapsed = TimeSpan.Zero;
        _overlayViewModel.SetRecordingElapsed(TimeSpan.Zero);

        _recordingTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        _recordingTimer.Tick -= OnRecordingTimerTick;
        _recordingTimer.Tick += OnRecordingTimerTick;
        _recordingTimer.Start();

        OnPropertyChanged(nameof(StartStopRecordText));
    }

    private void StopRecordingTimer()
    {
        if (_recordingTimer is not null)
            _recordingTimer.Stop();

        _recordingStopwatch.Reset();
        _recordingElapsed = TimeSpan.Zero;
        _overlayViewModel.SetRecordingElapsed(null);
        OnPropertyChanged(nameof(StartStopRecordText));
    }

    private void OnRecordingTimerTick(object? sender, EventArgs e)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double tickGapMs = _recordingTimerLastTickTimestamp == 0
            ? 0
            : (nowTicks - _recordingTimerLastTickTimestamp) * 1000.0 / Stopwatch.Frequency;
        _recordingTimerLastTickTimestamp = nowTicks;
        _recordingTimerTickCount++;

        TimeSpan stopwatchElapsed = _recordingStopwatch.Elapsed;
        TimeSpan wallElapsed = DateTimeOffset.Now - _recordingStartedAt;
        double driftMs = wallElapsed.TotalMilliseconds - stopwatchElapsed.TotalMilliseconds;
        _recordingElapsed = stopwatchElapsed;
        _overlayViewModel.SetRecordingElapsed(stopwatchElapsed);
        OnPropertyChanged(nameof(StartStopRecordText));

        bool shouldLog = tickGapMs > 750 || Math.Abs(driftMs) > 750 || nowTicks - _recordingTimerLastLogTimestamp > Stopwatch.Frequency * 5;
        if (shouldLog)
        {
            _recordingTimerLastLogTimestamp = nowTicks;
            AppLogger.Info($"UI recording timer diagnostics | Tick={_recordingTimerTickCount} | TickGapMs={tickGapMs:0.##} | StopwatchElapsedMs={stopwatchElapsed.TotalMilliseconds:0.##} | WallElapsedMs={wallElapsed.TotalMilliseconds:0.##} | DriftMs={driftMs:0.##} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
        }
    }

    private void StartAudioDeviceMonitoring()
    {
        if (_audioDeviceEnumerator is not null)
        {
            return;
        }

        try
        {
            _audioDeviceEnumerator =
                new MMDeviceEnumerator();

            _audioDeviceNotificationClient =
                new AudioDeviceNotificationClient(
                    QueueAudioDeviceRefresh);

            _audioDeviceEnumerator
                .RegisterEndpointNotificationCallback(
                    _audioDeviceNotificationClient);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                $"Audio device monitoring failed: {ex}");
        }
    }

    private void QueueAudioDeviceRefresh()
    {
        var dispatcher =
            System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null ||
            dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            _audioDeviceRefreshCts?.Cancel();
            _audioDeviceRefreshCts?.Dispose();

            _audioDeviceRefreshCts =
                new CancellationTokenSource();

            _ = RefreshAudioDevicesAfterDelayAsync(
                _audioDeviceRefreshCts.Token);
        });
    }

    private async Task RefreshAudioDevicesAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                250,
                cancellationToken);

            cancellationToken
                .ThrowIfCancellationRequested();

            LoadAudioDevices();

            AppLogger.Debug(
                "Audio device list refreshed.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                $"Audio device refresh failed: {ex}");
        }
    }

    private void StopAudioDeviceMonitoring()
    {
        if (_audioLevelTimer is not null)
        {
            _audioLevelTimer.Stop();
            _audioLevelTimer = null;
        }

        DisposeAudioMeterDevices();

        _audioDeviceRefreshCts?.Cancel();
        _audioDeviceRefreshCts?.Dispose();
        _audioDeviceRefreshCts = null;

        if (_audioDeviceEnumerator is not null &&
            _audioDeviceNotificationClient is not null)
        {
            try
            {
                _audioDeviceEnumerator
                    .UnregisterEndpointNotificationCallback(
                        _audioDeviceNotificationClient);
            }
            catch
            {
            }
        }

        _audioDeviceNotificationClient = null;

        _audioDeviceEnumerator?.Dispose();
        _audioDeviceEnumerator = null;
    }

    private static void AddDevice(ObservableCollection<string> list, Dictionary<string, string?> map, string name, string? id)
    {
        string unique = name;
        int suffix = 2;
        while (map.ContainsKey(unique)) unique = $"{name} ({suffix++})";
        list.Add(unique);
        map[unique] = id;
    }

    private static string? FindDeviceName(Dictionary<string, string?> map, string? id) =>
        map.FirstOrDefault(pair => pair.Value == id).Key;

    private async Task SelectFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Skadi save folder",
            InitialDirectory = Directory.Exists(SaveFolder) ? SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() == true)
        {
            SaveFolder = dialog.FolderName;
            LogEvent($"Save folder: {Path.GetFileName(SaveFolder)}");
        }
        await Task.CompletedTask;
    }

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveFolder);

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = SaveFolder,
                    UseShellExecute = true
                });

            LogEvent($"Opened folder: {Path.GetFileName(SaveFolder)}");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            LogEvent("Could not open save folder", warning: true);
        }
    }

    // Створення та збереження скріншота ...
    private async Task SaveScreenshotAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        AppLogger.Info($"UI state | Action=SaveScreenshot clicked | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CanEdit={CanEditSettings} | Target={_selectedTargetValue} | SaveFolder={SaveFolder}");

        _screenshotCaptureCts?.Cancel();
        _screenshotCaptureCts?.Dispose();
        var screenshotCts = new CancellationTokenSource();
        _screenshotCaptureCts = screenshotCts;

        try
        {
            bool restoreUiAfterCapture =
                IsSkadiPanelVisible();

            AppLogger.Info($"Screenshot command | PanelVisible={restoreUiAfterCapture}");

            string? path =
                await _screenshotSelection.CaptureSelectionAsync(
                    SaveFolder,
                    _toggleUi,
                    restoreUiAfterCapture,
                    screenshotCts.Token,
                    allowBlockingMemoryCleanup: !IsContinuousRecording && !BufferEnabled);

            if (path is null)
            {
                AppLogger.Info($"Screenshot command canceled | ElapsedMs={stopwatch.ElapsedMilliseconds}");
                LogEvent(
                    "Screenshot canceled");

                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                AppLogger.Error(
                    nameof(MainViewModel),
                    $"Screenshot command unavailable | ElapsedMs={stopwatch.ElapsedMilliseconds}");

                LogEvent(
                    "Screenshot capture unavailable",
                    warning: true);

                _notifications.Show(
                    "Screenshot unavailable");

                return;
            }

            AppLogger.Info($"Screenshot command completed | Path={path} | ElapsedMs={stopwatch.ElapsedMilliseconds}");

            LogEvent(
                $"Screenshot saved: {Path.GetFileName(path)}");

            _notifications.Show(
                "Screenshot saved");
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info($"Screenshot command canceled by display topology change | ElapsedMs={stopwatch.ElapsedMilliseconds}");
            LogEvent(
                "Screenshot canceled");
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                $"Screenshot command failed after {stopwatch.ElapsedMilliseconds} ms: {ex}");

            LogEvent(
                "Screenshot failed",
                warning: true);

            _notifications.Show(
                "Screenshot failed");
        }
        finally
        {
            if (ReferenceEquals(_screenshotCaptureCts, screenshotCts))
                _screenshotCaptureCts = null;

            screenshotCts.Dispose();
        }
    }
    // ...Створення та збереження скріншота

    private static bool IsSkadiPanelVisible()
    {
        return System.Windows.Application.Current?
            .Windows
            .OfType<MainWindow>()
            .FirstOrDefault()?
            .IsPanelVisible == true;
    }

    // Збереження replay за командою користувача ...
    private async Task SaveRecordAsync()
    {
        AppLogger.Info($"UI state | Action=SaveReplay clicked | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Mode={CaptureMode} | Frames={_capture.CapturedFrames}");

        try
        {
            if (BufferEnabled)
            {
                _settingsCts?.Cancel();
                _restartPending = false;

                ApplyToSettings();
                _settings.Save();

                AppLogger.Info(
                    $"UI state | Action=SaveReplay sync-settings | Buffer={BufferEnabled} | " +
                    $"Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | " +
                    $"Frames={_capture.CapturedFrames}");

                await _capture.ApplySettingsAsync(
                    _settings,
                    restartPipeline: false);
            }

            string path = await _capture.SaveRecordAsync();

            AppLogger.Info($"UI state | Action=SaveReplay success | Path={Path.GetFileName(path)} | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");

            LogEvent($"Replay saved: {Path.GetFileName(path)}");
            _notifications.Show(_captureMode == "Audio" ? "MP3 saved" : "Replay saved");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            AppLogger.Info($"UI state | Action=SaveReplay failed | Message={ex.Message} | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");

            LogEvent(ex.Message);
            _notifications.Show("Replay save failed");
        }
    }
    // ...Збереження replay за командою користувача

    private void OnContinuousRecordingStopping(object? sender, EventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _isRecordStateChanging = true;
            IsContinuousRecording = false;
            AppLogger.Info(
                $"UI state | Action=Recording forced-stop-started | Buffer={BufferEnabled} | " +
                $"CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
        });
    }

    private void OnContinuousRecordingStopped(object? sender, ContinuousRecordingStoppedEventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _isRecordStateChanging = false;
            IsContinuousRecording = false;

            AppLogger.Info(
                $"UI state | Action=Recording forced-stop | Message={eventArgs.Message} | " +
                $"Path={Path.GetFileName(eventArgs.Path ?? string.Empty)} | " +
                $"Error={eventArgs.Error?.Message} | Buffer={BufferEnabled} | " +
                $"CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");

            LogEvent(eventArgs.Message, warning: true);
            _notifications.Show(eventArgs.Message);
        });
    }
    // Запуск або зупинка безперервного запису з UI ...
    private async Task ToggleContinuousRecordingAsync()
    {
        if (_isRecordStateChanging)
        {
            AppLogger.Info($"UI state | Action=Recording click ignored-state-changing | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
            return;
        }

        AppLogger.Info($"UI state | Action=Recording clicked | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Mode={CaptureMode} | Frames={_capture.CapturedFrames}");
        _isRecordStateChanging = true;
        bool wasRecording = IsContinuousRecording;

        try
        {
            if (wasRecording)
            {
                string path = await _capture.StopContinuousRecordingAsync();
                AppLogger.Info($"UI state | Action=StopRecording success-before-ui-state | Path={Path.GetFileName(path)} | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
                IsContinuousRecording = false;
                LogEvent($"Record saved: {Path.GetFileName(path)}");
                _notifications.Show("Record saved");
                return;
            }

            ApplyToSettings();
            _settings.Save();
            LogEvent("Recording starting");
            await _capture.StartContinuousRecordingAsync();
            AppLogger.Info($"UI state | Action=StartRecording success-before-ui-state | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
            IsContinuousRecording = true;
            LogEvent("Recording started");
            _notifications.Show("Recording started");
        }
        catch (Exception ex)
        {
            IsContinuousRecording =
                wasRecording &&
                _capture.IsContinuousRecording &&
                !ex.Message.Contains("not active", StringComparison.OrdinalIgnoreCase);

            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            LogEvent(
                string.IsNullOrWhiteSpace(ex.Message)
                    ? wasRecording ? "Recording stop failed" : "Recording start failed"
                    : ex.Message,
                warning: true);
                        _notifications.Show(
                string.Equals(ex.Message, "Disk is full", StringComparison.Ordinal)
                    ? "Disk is full"
                    : wasRecording ? "Recording stop failed" : "Recording start failed");
        }
        finally
        {
            AppLogger.Info($"UI state | Action=Recording command finished | Buffer={BufferEnabled} | Recording={IsContinuousRecording} | CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");
            _isRecordStateChanging = false;
        }
    }
    // ...Запуск або зупинка безперервного запису з UI

    // Реєстрація глобальних гарячих клавіш ...
    private void ApplyHotkeys()
    {
        try
        {
            var rejected =
                _hotkeys.RegisterAll(
                    ScreenshotHotkey,
                    RecordHotkey,
                    StartStopRecordHotkey,
                    ToggleUiHotkey);

            if (rejected.Count > 0)
            {
                if (rejected.Contains(GlobalHotkeyService.ScreenshotId))
                    ScreenshotHotkey = string.Empty;
                if (rejected.Contains(GlobalHotkeyService.SaveRecordId))
                    RecordHotkey = string.Empty;
                if (rejected.Contains(GlobalHotkeyService.StartStopRecordId))
                    StartStopRecordHotkey = string.Empty;
                if (rejected.Contains(GlobalHotkeyService.ToggleUiId))
                    ToggleUiHotkey = string.Empty;

                _hotkeys.RegisterAll(
                    ScreenshotHotkey,
                    RecordHotkey,
                    StartStopRecordHotkey,
                    ToggleUiHotkey);

                LogEvent("Hotkey is unavailable", warning: true);
            }

            _updateTrayHotkeys(ScreenshotHotkey, RecordHotkey);
            QueueSettingsUpdate(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), ex.ToString());
            LogEvent("Hotkey registration failed");
        }
    }
    // ...Реєстрація глобальних гарячих клавіш

    private void QueueSettingsUpdate(bool restartCapture)
    {
        if (_initializing) return;
        _restartPending |= restartCapture;
        _settingsCts?.Cancel();
        _settingsCts = new CancellationTokenSource();
        _ = ApplySettingsAfterDelayAsync(_settingsCts.Token);
    }

    // Відкладене застосування змінених налаштувань ...
    private async Task ApplySettingsAfterDelayAsync(
    CancellationToken token)
    {
        try
        {
            await Task.Delay(900, token);

            token.ThrowIfCancellationRequested();

            bool restart = _restartPending;
            _restartPending = false;

            if (restart && IsContinuousRecording)
            {
                _restartPending = true;
                LogEvent(
                    "Stop recording to change settings",
                    warning: true);
                return;
            }

            // Bindings UI thread.
            ApplyToSettings();
            _settings.Save();

            await Task.Run(
                () => _capture.ApplySettingsAsync(
                    _settings,
                    restart),
                token);
        }
        catch (OperationCanceledException){}
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                ex.ToString());

            LogEvent(
                "Could not apply settings");
        }
    }
    // ...Відкладене застосування змінених налаштувань

    // Негайне застосування змінених налаштувань ...
    private async Task ApplySettingsImmediatelyAsync(
        bool restartCapture)
    {
        try
        {
            if (restartCapture && IsContinuousRecording)
            {
                LogEvent(
                    "Stop recording to change settings",
                    warning: true);
                return;
            }

            ApplyToSettings();
            _settings.Save();

            await Task.Run(
                () => _capture.ApplySettingsAsync(
                    _settings,
                    restartCapture));
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                ex.ToString());

            LogEvent(
                "Could not apply settings",
                warning: true);
        }
    }
    // ...Негайне застосування змінених налаштувань

    // Негайне застосування стану replay buffer ...
    private async Task ApplyBufferStateImmediatelyAsync()
    {
        try
        {
            ApplyToSettings();
            _settings.Save();

            bool restartCapture = !IsContinuousRecording;

            AppLogger.Info(
                $"UI state | Action=BufferApplyImmediate | Buffer={BufferEnabled} | " +
                $"Restart={restartCapture} | Recording={IsContinuousRecording} | " +
                $"CaptureRecording={_capture.IsContinuousRecording} | Frames={_capture.CapturedFrames}");

            await Task.Run(
                () => _capture.ApplySettingsAsync(
                    _settings,
                    restartCapture));
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(MainViewModel),
                ex.ToString());

            LogEvent(
                "Could not apply buffer state",
                warning: true);
        }
    }
    // ...Негайне застосування стану replay buffer

    private void ApplyToSettings()
    {
        _settings.Fps = NormalizeFrameRate(FrameRate);
        _settings.BufferSeconds = BufferDuration;
        _settings.Resolution = ToStoredResolution(Resolution);
        _settings.BufferEnabled = BufferEnabled;
        _settings.CaptureMode = ToStoredMode(CaptureMode);
        _settings.CaptureTarget = _selectedTargetValue;
        _settings.VideoQuality = Quality switch { "Low" => 50, "Medium" => 70, _ => 90 };
        _settings.RecordSystemAudio = SystemAudioEnabled;
        _settings.RecordMicrophone = MicrophoneEnabled;
        _settings.SystemAudioDeviceId = _systemDeviceIds.GetValueOrDefault(SystemAudioDevice);
        _settings.MicDeviceId = _microphoneDeviceIds.GetValueOrDefault(MicrophoneDevice);
        _settings.SystemAudioVolume = (int)Math.Round(_lastSystemVolume);
        _settings.MicVolume = (int)Math.Round(_lastMicrophoneVolume);
        _settings.HotkeyScreenshot = ScreenshotHotkey;
        _settings.HotkeySaveVideo = RecordHotkey;
        _settings.HotkeyStartStopRecord = StartStopRecordHotkey;
        _settings.HotkeyToggleUI = ToggleUiHotkey;
        _settings.SaveFolder = SaveFolder;
        _settings.HudMode = HudMode;
    }

    private void UpdateCaptureSectionState(
    string mode,
    bool immediate)
    {
        bool settingsAvailable =
            CanEditSettings;

        bool video =
            settingsAvailable &&
            mode is "Combined" or "Video";

        bool audio =
            settingsAvailable &&
            mode is "Combined" or "Audio";

        int transitionVersion =
            ++_captureSectionTransitionVersion;

        if (video)
            IsVideoInputEnabled = true;

        if (audio)
            IsAudioInputEnabled = true;

        IsVideoSectionActive = video;
        IsAudioSectionActive = audio;

        if (immediate)
        {
            IsVideoInputEnabled = video;
            IsAudioInputEnabled = audio;
            return;
        }

        _ = CompleteCaptureSectionTransitionAsync(
            mode,
            video,
            audio,
            transitionVersion);
    }

    private async Task CompleteCaptureSectionTransitionAsync(
    string mode,
    bool video,
    bool audio,
    int transitionVersion)
    {
        await Task.Delay(
            TransitionMilliseconds);

        if (_captureMode != mode)
            return;

        if (_captureSectionTransitionVersion !=
            transitionVersion)
        {
            return;
        }

        IsVideoInputEnabled = video;
        IsAudioInputEnabled = audio;
    }

    private async Task UpdateDeviceInputAsync(bool systemAudio, bool enabled)
    {
        if (enabled) return;
        await Task.Delay(TransitionMilliseconds);
        if (systemAudio && !SystemAudioEnabled) IsSystemAudioInputEnabled = false;
        if (!systemAudio && !MicrophoneEnabled) IsMicrophoneInputEnabled = false;
    }

    private async Task AnimateVolumeAsync(bool systemAudio, double target)
    {
        var previous = systemAudio ? _systemVolumeAnimationCts : _microphoneVolumeAnimationCts;
        previous?.Cancel();
        previous?.Dispose();
        var cts = new CancellationTokenSource();
        if (systemAudio) _systemVolumeAnimationCts = cts; else _microphoneVolumeAnimationCts = cts;
        double start = systemAudio ? _systemVolume : _microphoneVolume;
        try
        {
            for (int frame = 1; frame <= 14; frame++)
            {
                await Task.Delay(TransitionMilliseconds / 14, cts.Token);
                double progress = frame / 14d;
                double smooth = progress * progress * (3 - 2 * progress);
                double value = start + ((target - start) * smooth);
                if (systemAudio) SetProperty(ref _systemVolume, value, nameof(SystemVolume));
                else SetProperty(ref _microphoneVolume, value, nameof(MicrophoneVolume));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CycleVideoSetting(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return;
        string[] parts = parameter.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int direction)) return;
        switch (parts[0])
        {
            case "Resolution": Resolution = Cycle(Resolutions, Resolution, direction); break;
            case "Quality": Quality = Cycle(["Low", "Medium", "High"], Quality, direction); break;
            case "FrameRate": FrameRate = Cycle(FrameRates, FrameRate, direction); break;
            case "BufferDuration": BufferDuration = Cycle(BufferDurations, BufferDuration, direction); break;
            case "HudMode": HudMode = Cycle(["None", "Timer", "System Info"], HudMode, direction); break;
        }
    }

    private void ApplyHudMode()
    {
        _overlayViewModel.SetHudMode(HudMode);
        if (HudMode == "None")
            _overlay.Hide();
        else
            _overlay.Show();
    }

    private static T Cycle<T>(IReadOnlyList<T> values, T current, int direction)
    {
        int index = -1;
        for (int i = 0; i < values.Count; i++)
            if (EqualityComparer<T>.Default.Equals(values[i], current)) { index = i; break; }
        if (index < 0) index = 0;
        return values[(index + Math.Sign(direction) + values.Count) % values.Count];
    }

    private static string FromStoredMode(string mode) => mode == "VideoAudio" ? "Combined" : mode;
    private static string ToStoredMode(string mode) => mode == "Combined" ? "VideoAudio" : mode;
    private static int NormalizeFrameRate(int value) => value <= 30 ? 30 : 60;
    private static bool IsWindows10()
    {
        Version version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() && version.Major == 10 && version.Build < 22000;
    }

    private string FromStoredResolution(string resolution) => resolution switch
    {
        "720p" => "1280x720",
        "1080p" => "1920x1080",
        "1440p" => "2560x1440",
        _ => NativeResolutionLabel
    };

    private static string ToStoredResolution(string resolution) => resolution switch
    {
        "1280x720" => "720p",
        "1920x1080" => "1080p",
        "2560x1440" => "1440p",
        _ => "Native"
    };

    public async Task ToggleUiAsync() => await _toggleUi();
    public void ExecuteScreenshot() => SaveScreenshotCommand.Execute(null);
    public void ExecuteRecord() => SaveRecordCommand.Execute(null);
    public void ExecuteStartStopRecord() => StartStopRecordCommand.Execute(null);

    private void ShowSettingsLockedMessage()
    {
        if (IsContinuousRecording)
        {
            LogEvent(
                "Stop recording to change settings",
                warning: true);

            return;
        }

        LogEvent(
            "Disable Buffer to change settings",
            warning: true);
    }
    private async void LogEvent(string message, bool warning = false)
    {
        _eventCts?.Cancel();
        _eventCts?.Dispose();

        _eventCts = new CancellationTokenSource();

        EventMessage = message;
        EventIsWarning = warning;
        IsEventVisible = true;

        try
        {
            await Task.Delay(2600, _eventCts.Token);
            IsEventVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Зупинка UI monitoring та звільнення ресурсів моделі ...
    public void Dispose()
    {
        if (_recordingTimer is not null)
        {
            _recordingTimer.Stop();
            _recordingTimer.Tick -= OnRecordingTimerTick;
            _recordingTimer = null;
        }

        StopDisplayChangeMonitoring();
        _screenshotSelection.Dispose();
        ScreenCapturer.ReleaseScreenshotCaptureResources();
        _capture.ContinuousRecordingStopping -= OnContinuousRecordingStopping;
        _capture.ContinuousRecordingStopped -= OnContinuousRecordingStopped;

        StopAudioDeviceMonitoring();

        _settingsCts?.Cancel();
        _eventCts?.Cancel();
        _systemVolumeAnimationCts?.Cancel();
        _microphoneVolumeAnimationCts?.Cancel();

        _settingsCts?.Dispose();
        _eventCts?.Dispose();
        _systemVolumeAnimationCts?.Dispose();
        _microphoneVolumeAnimationCts?.Dispose();
    }
    // ...Зупинка UI monitoring та звільнення ресурсів моделі
}
