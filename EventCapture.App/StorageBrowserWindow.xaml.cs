using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EventCapture.App.Services;
using EventCapture.Core.Diagnostics;
using WpfBrush = System.Windows.Media.Brush;
using WpfImageSource = System.Windows.Media.ImageSource;

namespace EventCapture.App;

public partial class StorageBrowserWindow : Window
{
    private const int ExtendedWindowStyle = -20;
    private const long ToolWindowStyle = 0x00000080;
    private const long AppWindowStyle = 0x00040000;
    private const int LowLevelMouseHook = 14;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int MiddleButtonDown = 0x0207;
    private const int ExtraButtonDown = 0x020B;

    private readonly ObservableCollection<StorageFolderItem> _folders = [];
    private readonly ObservableCollection<StorageNavigationItem>
        _quickAccessItems = [];
    private readonly ObservableCollection<StorageNavigationItem>
        _thisPcItems = [];
    private string _currentPath = string.Empty;
    private bool _busy;
    private int _navigationVersion;
    private IntPtr _windowHandle;
    private IntPtr _mouseHook;
    private LowLevelMouseProcedure? _mouseHookProcedure;
    private TaskCompletionSource<SmbConnectionService.SmbCredentials?>?
        _credentialRequest;

    public StorageBrowserWindow(string initialPath)
    {
        InitializeComponent();
        WindowFrame.BorderBrush =
            ((WpfBrush)FindResource("AccentBrush")).CloneCurrentValue();
        FolderList.ItemsSource = _folders;
        QuickAccessList.ItemsSource = _quickAccessItems;
        ThisPcList.ItemsSource = _thisPcItems;
        LoadQuickAccess();
        LoadThisPc();
        PathBox.Text = initialPath;
        SourceInitialized += (_, _) => InitializeNativeWindow();
        Closed += (_, _) =>
        {
            CompleteAuthorization(null);
            RemoveModalMouseGuard();
        };
        Loaded += async (_, _) => await NavigateAsync(initialPath, connectSmb: false);
    }

    public string SelectedPath { get; private set; } = string.Empty;

    private async Task NavigateAsync(string rawPath, bool connectSmb)
    {
        if (_busy)
            return;

        int version = ++_navigationVersion;
        SetBusy(true, "Opening storage...");
        try
        {
            string path = NormalizePath(rawPath);
            if (IsUncPath(path) && connectSmb)
                path = await SmbConnectionService.ConnectAsync(
                    path,
                    RequestCredentialsAsync);
            else if (!Directory.Exists(path))
                throw new DirectoryNotFoundException("Storage path not found.");

            string[] directories = await Task.Run(
                () => Directory.EnumerateDirectories(path)
                    .OrderBy(folder => Path.GetFileName(folder), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray());
            if (version != _navigationVersion)
                return;

            _folders.Clear();
            foreach (string directory in directories)
            {
                _folders.Add(new StorageFolderItem(
                    Path.GetFileName(directory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)),
                    directory));
            }

            _currentPath = path;
            PathBox.Text = path;
            EmptyFolderText.Visibility =
                _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(
                StoragePathService.IsRemote(path)
                    ? "Network storage opened"
                    : "Local storage opened",
                success: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Authorization canceled", success: false);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SelectStorageAsync()
    {
        if (_busy)
            return;

        SetBusy(true, "Checking write access...");
        try
        {
            string requestedPath =
                FolderList.SelectedItem is StorageFolderItem selectedFolder
                    ? selectedFolder.Path
                    : PathBox.Text;
            string path = NormalizePath(requestedPath);
            if (IsUncPath(path))
            {
                path = await SmbConnectionService.ConnectAsync(
                    path,
                    RequestCredentialsAsync);
            }
            else
            {
                await Task.Run(() => VerifyLocalWriteAccess(path));
            }

            SelectedPath = path;
            SetStatus("Storage changed", success: true);
            AppLogger.Info($"Storage changed | Path={path}");
            await Task.Delay(350);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Authorization canceled", success: false);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string NormalizePath(string rawPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        string expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        if (IsUncPath(expanded))
            return SmbConnectionService.NormalizeUncPath(expanded);

        string fullPath = Path.GetFullPath(expanded);
        string? root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(
                   fullPath.TrimEnd(Path.DirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    private static void VerifyLocalWriteAccess(string path)
    {
        string probePath = Path.Combine(
            path,
            $".skadi-access-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(path);
            using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough))
            {
                stream.WriteByte(0x53);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probePath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(probePath);
            throw new UnauthorizedAccessException(
                "Access denied. You do not have permission to write to this folder.");
        }
        catch
        {
            TryDelete(probePath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private void LoadThisPc()
    {
        _thisPcItems.Clear();
        try
        {
            foreach ((string displayName, string path) in
                     EnumerateShellFileSystemFolders(
                         "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
                         requireExistingDirectory: false))
            {
                _thisPcItems.Add(
                    new StorageNavigationItem(
                        displayName,
                        path,
                        GetShellIcon(path)));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(StorageBrowserWindow),
                $"Could not read Explorer This PC: {ex}");
        }

        if (_thisPcItems.Count == 0)
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives()
                         .Where(drive => drive.IsReady)
                         .OrderBy(drive => drive.Name))
            {
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                string path = drive.RootDirectory.FullName;
                _thisPcItems.Add(
                    new StorageNavigationItem(
                        label,
                        path,
                        GetShellIcon(path)));
            }
        }
    }

    private void LoadQuickAccess()
    {
        _quickAccessItems.Clear();
        try
        {
            foreach ((string displayName, string path) in
                     EnumerateShellFileSystemFolders(
                         "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}",
                         requireExistingDirectory: true))
            {
                _quickAccessItems.Add(
                    new StorageNavigationItem(
                        displayName,
                        path,
                        GetShellIcon(path)));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                nameof(StorageBrowserWindow),
                $"Could not read Explorer Quick access: {ex}");
        }

        if (_quickAccessItems.Count == 0)
            LoadDefaultQuickAccess();
    }

    private static IEnumerable<(string DisplayName, string Path)>
        EnumerateShellFileSystemFolders(
            string shellNamespace,
            bool requireExistingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException(
                "Windows Shell is not available.");
        object? shellObject = null;
        object? folderObject = null;
        object? itemsObject = null;
        var result = new List<(string DisplayName, string Path)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "Could not start Windows Shell.");
            dynamic shell = shellObject;
            folderObject = shell.NameSpace(shellNamespace);
            if (folderObject is null)
                return result;

            dynamic folder = folderObject;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            int count = items.Count;
            for (int index = 0; index < count; index++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(index);
                    if (itemObject is null)
                        continue;

                    dynamic item = itemObject;
                    if (!(bool)item.IsFolder)
                        continue;

                    string path = ((string?)item.Path ?? string.Empty).Trim();
                    if (!IsFileSystemPath(path) ||
                        (requireExistingDirectory &&
                         !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                         !Directory.Exists(path)) ||
                        !paths.Add(path))
                        continue;

                    string name = ((string?)item.Name ?? string.Empty).Trim();
                    result.Add((
                        string.IsNullOrWhiteSpace(name)
                            ? Path.GetFileName(path.TrimEnd('\\'))
                            : name,
                        path));
                }
                finally
                {
                    ReleaseComObject(itemObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(itemsObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellObject);
        }

        return result;
    }

    private void LoadDefaultQuickAccess()
    {
        AddDefaultQuickAccess(
            "Desktop",
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory));
        AddDefaultQuickAccess(
            "Downloads",
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads"));
        AddDefaultQuickAccess(
            "Documents",
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments));
        AddDefaultQuickAccess(
            "Pictures",
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyPictures));
        AddDefaultQuickAccess(
            "Music",
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyMusic));
        AddDefaultQuickAccess(
            "Videos",
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyVideos));
    }

    private void AddDefaultQuickAccess(string displayName, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _quickAccessItems.Add(
                new StorageNavigationItem(
                    displayName,
                    path,
                    GetShellIcon(path)));
        }
    }

    private static bool IsFileSystemPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        Path.IsPathFullyQualified(path);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static WpfImageSource? GetShellIcon(string path)
    {
        var fileInfo = new ShellFileInfo();
        IntPtr result = SHGetFileInfo(
            path,
            0,
            ref fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellIcon | SmallIcon);
        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
            return null;

        try
        {
            BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            icon.Freeze();
            return icon;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    private const uint ShellIcon = 0x000000100;
    private const uint SmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        IntPtr windowHandle,
        int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelMouseProcedure callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private string? GetParentPath()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
            return null;
        if (IsUncPath(_currentPath))
        {
            string[] parts = _currentPath.Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2)
                return null;
        }

        return Directory.GetParent(_currentPath)?.FullName;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        PathBox.IsReadOnly = busy;
        GoButton.IsEnabled = !busy;
        UpButton.IsEnabled = !busy;
        FolderList.IsHitTestVisible = !busy;
        QuickAccessList.IsHitTestVisible = !busy;
        ThisPcList.IsHitTestVisible = !busy;
        UseStorageButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            SetStatus(status, success: false);
    }

    private void SetStatus(string message, bool success)
    {
        StatusText.Text = message;
        var brush = (WpfBrush)FindResource(
            success ? "AccentBrush" : "SecondaryTextBrush");
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private void ShowError(Exception exception)
    {
        Exception error = exception is AggregateException aggregate
            ? aggregate.GetBaseException()
            : exception;
        string message = error switch
        {
            UnauthorizedAccessException => "Access denied.",
            DirectoryNotFoundException => "Storage does not exist.",
            Win32Exception win32 when win32.NativeErrorCode is 53 or 67 =>
                "Storage does not exist.",
            Win32Exception win32 when win32.NativeErrorCode is 86 or 1326 =>
                "The user name or password is incorrect.",
            InvalidOperationException => error.Message,
            IOException io when (io.HResult & 0xFFFF) is 2 or 3 or 53 or 67 =>
                "Storage does not exist.",
            IOException io when (io.HResult & 0xFFFF) == 5 =>
                "Access denied.",
            IOException => "Could not access storage.",
            _ => $"Could not connect to storage: {error.Message}"
        };

        StatusText.Text = message;
        StatusText.Foreground = (WpfBrush)FindResource("DangerBrush");
        StatusDot.Fill = (WpfBrush)FindResource("DangerBrush");
        AppLogger.Error(nameof(StorageBrowserWindow), $"{message} | {error}");
    }

    private Task<SmbConnectionService.SmbCredentials?> RequestCredentialsAsync(
        string shareRoot)
    {
        _credentialRequest?.TrySetResult(null);
        _credentialRequest =
            new TaskCompletionSource<SmbConnectionService.SmbCredentials?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        AuthorizationMessage.Text =
            $"Enter the account that can access {shareRoot}.";
        AuthorizationUserName.Text = string.Empty;
        AuthorizationPassword.Clear();
        AuthorizationError.Visibility = Visibility.Collapsed;
        AuthorizationPanel.Visibility = Visibility.Visible;
        AuthorizationUserName.Focus();
        return _credentialRequest.Task;
    }

    private void CompleteAuthorization(
        SmbConnectionService.SmbCredentials? credentials)
    {
        TaskCompletionSource<SmbConnectionService.SmbCredentials?>? request =
            _credentialRequest;
        _credentialRequest = null;
        AuthorizationPanel.Visibility = Visibility.Collapsed;
        AuthorizationUserName.Text = string.Empty;
        AuthorizationPassword.Clear();
        AuthorizationError.Visibility = Visibility.Collapsed;
        request?.TrySetResult(credentials);
    }

    private void AuthorizationConnect_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        string userName = AuthorizationUserName.Text.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            ShowAuthorizationError("Enter a user name.");
            AuthorizationUserName.Focus();
            return;
        }
        if (AuthorizationPassword.SecurePassword.Length == 0)
        {
            ShowAuthorizationError("Enter a password.");
            AuthorizationPassword.Focus();
            return;
        }

        using System.Security.SecureString securePassword =
            AuthorizationPassword.SecurePassword;
        var credentials = new SmbConnectionService.SmbCredentials(
            userName,
            securePassword);
        CompleteAuthorization(credentials);
    }

    private void AuthorizationCancel_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        CompleteAuthorization(null);

    private void AuthorizationField_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (IsInitialized)
            AuthorizationError.Visibility = Visibility.Collapsed;
    }

    private void ShowAuthorizationError(string message)
    {
        AuthorizationError.Text = message;
        AuthorizationError.Visibility = Visibility.Visible;
    }

    private async void Go_Click(object sender, RoutedEventArgs eventArgs) =>
        await NavigateAsync(PathBox.Text, connectSmb: true);

    private async void PathBox_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
            return;
        eventArgs.Handled = true;
        await NavigateAsync(PathBox.Text, connectSmb: true);
    }

    private void PathBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs eventArgs)
    {
        if (IsInitialized && !_busy)
        {
            FolderList.SelectedItem = null;
            SetStatus("Press Go to open this path", success: false);
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? parent = GetParentPath();
        if (parent is not null)
            await NavigateAsync(parent, connectSmb: false);
    }

    private async void QuickAccessList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (QuickAccessList.SelectedItem is not StorageNavigationItem item)
            return;

        await NavigateAsync(item.Path, connectSmb: false);
    }

    private async void ThisPcList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (ThisPcList.SelectedItem is StorageNavigationItem item)
            await NavigateAsync(item.Path, connectSmb: false);
    }

    private void NavigationScrollViewer_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs eventArgs)
    {
        if (sender is not System.Windows.Controls.ScrollViewer scrollViewer)
            return;

        double wheelSteps = eventArgs.Delta / 120.0;
        double distance =
            wheelSteps *
            Math.Max(1, SystemParameters.WheelScrollLines) *
            18;
        scrollViewer.ScrollToVerticalOffset(
            Math.Max(0, scrollViewer.VerticalOffset - distance));
        eventArgs.Handled = true;
    }

    private async void FolderList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (FolderList.SelectedItem is StorageFolderItem folder)
            await NavigateAsync(folder.Path, connectSmb: false);
    }

    private async void UseStorage_Click(object sender, RoutedEventArgs eventArgs) =>
        await SelectStorageAsync();

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (AuthorizationPanel.Visibility == Visibility.Visible)
        {
            CompleteAuthorization(null);
            return;
        }

        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void InitializeNativeWindow()
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        nint currentStyle =
            GetWindowLongPtr(_windowHandle, ExtendedWindowStyle);
        long style = currentStyle.ToInt64();
        style &= ~ToolWindowStyle;
        style |= AppWindowStyle;
        SetWindowLongPtr(
            _windowHandle,
            ExtendedWindowStyle,
            new nint(style));
        InstallModalMouseGuard();
    }

    private void InstallModalMouseGuard()
    {
        if (_mouseHook != IntPtr.Zero)
            return;

        _mouseHookProcedure = ModalMouseHook;
        _mouseHook = SetWindowsHookEx(
            LowLevelMouseHook,
            _mouseHookProcedure,
            GetModuleHandle(null),
            0);
        if (_mouseHook == IntPtr.Zero)
        {
            AppLogger.Error(
                nameof(StorageBrowserWindow),
                $"Could not install modal mouse guard. Win32={Marshal.GetLastWin32Error()}");
        }
    }

    private void RemoveModalMouseGuard()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseHookProcedure = null;
    }

    private IntPtr ModalMouseHook(
        int code,
        IntPtr message,
        IntPtr data)
    {
        if (code >= 0 &&
            IsMouseButtonDown(message.ToInt32()) &&
            GetForegroundWindow() == _windowHandle &&
            GetWindowRect(_windowHandle, out NativeRectangle rectangle))
        {
            LowLevelMouseData mouseData =
                Marshal.PtrToStructure<LowLevelMouseData>(data);
            if (!rectangle.Contains(mouseData.Point))
            {
                Dispatcher.BeginInvoke(NotifyModalAttention);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void NotifyModalAttention()
    {
        if (!IsVisible)
            return;

        SystemSounds.Beep.Play();
        var information = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = _windowHandle,
            Flags = 3,
            Count = 2,
            Timeout = 0
        };
        FlashWindowEx(ref information);
        AnimateWindowFrame();
        Activate();
    }

    private void AnimateWindowFrame()
    {
        if (WindowFrame.BorderBrush is not WpfBrush frameBrush)
            return;

        var animation =
            new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(440),
                FillBehavior =
                    System.Windows.Media.Animation.FillBehavior.Stop
            };
        animation.KeyFrames.Add(
            new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1,
                System.Windows.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.Zero)));
        animation.KeyFrames.Add(
            new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                0,
                System.Windows.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(100))));
        animation.KeyFrames.Add(
            new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1,
                System.Windows.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(220))));
        animation.KeyFrames.Add(
            new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                0,
                System.Windows.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(320))));
        animation.KeyFrames.Add(
            new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1,
                System.Windows.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(440))));

        frameBrush.BeginAnimation(
            WpfBrush.OpacityProperty,
            animation,
            System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private static bool IsMouseButtonDown(int message) =>
        message is LeftButtonDown or
            RightButtonDown or
            MiddleButtonDown or
            ExtraButtonDown;

    private delegate IntPtr LowLevelMouseProcedure(
        int code,
        IntPtr message,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public bool Contains(NativePoint point) =>
            point.X >= Left &&
            point.X < Right &&
            point.Y >= Top &&
            point.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseData
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private sealed record StorageFolderItem(string Name, string Path);
    private sealed record StorageNavigationItem(
        string DisplayName,
        string Path,
        WpfImageSource? Icon);
}
