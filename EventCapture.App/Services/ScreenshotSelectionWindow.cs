using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Xps.Packaging;
using DrawingRectangle = System.Drawing.Rectangle;
using ShapesRectangle = System.Windows.Shapes.Rectangle;
using WpfImage = System.Windows.Controls.Image;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfPoint = System.Windows.Point;

namespace EventCapture.App.Services;

internal sealed class ScreenshotSelectionWindow : Window
{
    private const int MinSelectionPixels = 12;
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(140);
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    private readonly DisplayMonitor _screen;
    private readonly Canvas _canvas;
    private readonly ShapesRectangle _selectionRectangle;
    private WpfImage? _frozenImage;
    private WriteableBitmap? _reusableBitmap;
    private Action<ScreenshotSelectionResult?>? _complete;
    private WpfPoint? _startPoint;
    private bool _completed;
    private bool _closed;

    private static int _liveWindowCount;
    private static int _reusableBitmapCount;
    internal static int LiveWindowCount => Volatile.Read(ref _liveWindowCount);
    internal static int ReusableBitmapCount => Volatile.Read(ref _reusableBitmapCount);

    public ScreenshotSelectionWindow(
     DisplayMonitor screen)
    {
        Interlocked.Increment(ref _liveWindowCount);
        _screen = screen;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = true;
        Cursor = WpfCursors.Cross;

        var root = new Grid();

        _frozenImage = new WpfImage
        {
            Stretch = Stretch.Fill
        };

        root.Children.Add(_frozenImage);

        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(WpfColor.FromArgb(92, 0, 0, 0))
        });

        _canvas = new Canvas { Background = WpfBrushes.Transparent };

        _selectionRectangle = new ShapesRectangle
        {
            Stroke = new SolidColorBrush(WpfColor.FromRgb(0x00, 0xC4, 0xA0)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(WpfColor.FromArgb(36, 0x00, 0xC4, 0xA0)),
            Visibility = Visibility.Collapsed
        };

        _canvas.Children.Add(_selectionRectangle);
        root.Children.Add(_canvas);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += (_, _) => Complete(null);
        KeyDown += OnKeyDown;
        Closed += OnClosed;
    }

    public void Prepare(
        Bitmap frozenImage,
        Action<ScreenshotSelectionResult?> complete)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        IsHitTestVisible = false;

        _complete = complete;
        _completed = false;
        _startPoint = null;
        _selectionRectangle.Visibility = Visibility.Collapsed;
        _selectionRectangle.Width = 0;
        _selectionRectangle.Height = 0;

        if (_reusableBitmap is null ||
            _reusableBitmap.PixelWidth != frozenImage.Width ||
            _reusableBitmap.PixelHeight != frozenImage.Height)
        {
            if (_reusableBitmap is null)
                Interlocked.Increment(ref _reusableBitmapCount);

            _reusableBitmap = new WriteableBitmap(
                frozenImage.Width,
                frozenImage.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);

            if (_frozenImage is not null)
                _frozenImage.Source = _reusableBitmap;
        }

        CopyBitmapPixels(frozenImage, _reusableBitmap);
    }

    public async Task ShowPreparedAsync()
    {
        Show();

        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Render);

        var animation = new DoubleAnimation(
            0,
            1,
            FadeInDuration)
        {
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseOut
            },
            FillBehavior = FillBehavior.Stop
        };

        Opacity = 1;
        BeginAnimation(OpacityProperty, animation);
        IsHitTestVisible = true;
        Activate();
        Focus();
    }

    public async Task ResetForReuseAsync()
    {
        ReleaseMouseCapture();
        IsHitTestVisible = false;

        if (IsVisible && Opacity > 0)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var animation = new DoubleAnimation(
                Opacity,
                0,
                FadeOutDuration)
            {
                EasingFunction = new SineEase
                {
                    EasingMode = EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.HoldEnd
            };

            animation.Completed += (_, _) => completion.TrySetResult();
            BeginAnimation(OpacityProperty, animation);
            await completion.Task;
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _startPoint = null;
        _complete = null;
        _selectionRectangle.Visibility = Visibility.Collapsed;

        Hide();
    }

    private static void CopyBitmapPixels(
        Bitmap source,
        WriteableBitmap destination)
    {
        var bounds = new System.Drawing.Rectangle(0, 0, source.Width, source.Height);
        BitmapData? data = null;

        try
        {
            data = source.LockBits(
                bounds,
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            if (data.Stride <= 0)
                throw new InvalidOperationException("Screenshot bitmap has an unsupported negative stride.");

            destination.WritePixels(
                new Int32Rect(0, 0, source.Width, source.Height),
                data.Scan0,
                checked(data.Stride * source.Height),
                data.Stride);
        }
        finally
        {
            if (data is not null)
                source.UnlockBits(data);
        }
    }

    private void OnClosed(
        object? sender,
        EventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            Interlocked.Decrement(ref _liveWindowCount);
        }

        Complete(null);

        if (_frozenImage is not null)
        {
            _frozenImage.Source = null;
            _frozenImage = null;
        }

        if (_reusableBitmap is not null)
        {
            _reusableBitmap = null;
            Interlocked.Decrement(ref _reusableBitmapCount);
        }

        Content = null;
        AppLogger.Info($"Screenshot selection window released | Device={_screen.DeviceName} | LiveWindows={LiveWindowCount}");
    }

    private void OnSourceInitialized(
        object? sender,
        EventArgs e)
    {
        IntPtr handle =
            new WindowInteropHelper(this).Handle;

        AppLogger.Info($"Screenshot selection window initialized | Device={_screen.DeviceName} | Bounds={_screen.Bounds}");

        var source =
            HwndSource.FromHwnd(handle);

        if (source?.CompositionTarget is not null)
        {
            Matrix fromDevice =
                source.CompositionTarget.TransformFromDevice;

            WpfPoint topLeft =
                fromDevice.Transform(
                    new WpfPoint(
                        _screen.Bounds.Left,
                        _screen.Bounds.Top));

            WpfPoint bottomRight =
                fromDevice.Transform(
                    new WpfPoint(
                        _screen.Bounds.Right,
                        _screen.Bounds.Bottom));

            Left = topLeft.X;
            Top = topLeft.Y;
            Width = bottomRight.X - topLeft.X;
            Height = bottomRight.Y - topLeft.Y;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            _screen.Bounds.Left,
            _screen.Bounds.Top,
            _screen.Bounds.Width,
            _screen.Bounds.Height,
            SwpShowWindow);
    }

    private void OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(_canvas);
        CaptureMouse();

        Canvas.SetLeft(_selectionRectangle, _startPoint.Value.X);
        Canvas.SetTop(_selectionRectangle, _startPoint.Value.Y);
        _selectionRectangle.Width = 0;
        _selectionRectangle.Height = 0;
        _selectionRectangle.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_startPoint is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        WpfPoint current =
            e.GetPosition(_canvas);

        double left =
            Math.Min(
                _startPoint.Value.X,
                current.X);

        double top =
            Math.Min(
                _startPoint.Value.Y,
                current.Y);

        double width =
            Math.Abs(
                current.X -
                _startPoint.Value.X);

        double height =
            Math.Abs(
                current.Y -
                _startPoint.Value.Y);

        Canvas.SetLeft(_selectionRectangle, left);
        Canvas.SetTop(_selectionRectangle, top);
        _selectionRectangle.Width = width;
        _selectionRectangle.Height = height;
    }

    private void OnMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            return;
        }

        ReleaseMouseCapture();

        WpfPoint endPoint =
            e.GetPosition(_canvas);

        DrawingRectangle selectedBounds =
            ToPhysicalRectangle(
                _startPoint.Value,
                endPoint);

        bool fullScreen =
            selectedBounds.Width < MinSelectionPixels ||
            selectedBounds.Height < MinSelectionPixels;

        if (fullScreen)
        {
            selectedBounds = _screen.Bounds;
        }

        Complete(
            new ScreenshotSelectionResult(
                $"Monitor|{_screen.DeviceName}",
                _screen.DeviceName,
                _screen.Bounds,
                selectedBounds,
                fullScreen));
    }

    private DrawingRectangle ToPhysicalRectangle(
        WpfPoint first,
        WpfPoint second)
    {
        var source =
            PresentationSource.FromVisual(this);

        Matrix toDevice =
            source?.CompositionTarget?.TransformToDevice ??
            Matrix.Identity;

        WpfPoint physicalFirst =
            toDevice.Transform(first);

        WpfPoint physicalSecond =
            toDevice.Transform(second);

        int left =
            _screen.Bounds.Left +
            (int)Math.Round(
                Math.Min(
                    physicalFirst.X,
                    physicalSecond.X));

        int top =
            _screen.Bounds.Top +
            (int)Math.Round(
                Math.Min(
                    physicalFirst.Y,
                    physicalSecond.Y));

        int right =
            _screen.Bounds.Left +
            (int)Math.Round(
                Math.Max(
                    physicalFirst.X,
                    physicalSecond.X));

        int bottom =
            _screen.Bounds.Top +
            (int)Math.Round(
                Math.Max(
                    physicalFirst.Y,
                    physicalSecond.Y));

        left = Math.Clamp(
            left,
            _screen.Bounds.Left,
            _screen.Bounds.Right);

        top = Math.Clamp(
            top,
            _screen.Bounds.Top,
            _screen.Bounds.Bottom);

        right = Math.Clamp(
            right,
            _screen.Bounds.Left,
            _screen.Bounds.Right);

        bottom = Math.Clamp(
            bottom,
            _screen.Bounds.Top,
            _screen.Bounds.Bottom);

        return DrawingRectangle.FromLTRB(
            left,
            top,
            Math.Max(left, right),
            Math.Max(top, bottom));
    }

    private void OnKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(null);
        }
    }

    private void Complete(
        ScreenshotSelectionResult? result)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        AppLogger.Info($"Screenshot selection completed | Device={_screen.DeviceName} | HasResult={result is not null} | FullScreen={result?.IsFullScreen.ToString() ?? "False"} | Selection={result?.SelectionBounds.ToString() ?? "None"}");
        _complete?.Invoke(result);
    }

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
