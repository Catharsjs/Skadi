using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EventCapture.App;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;

    private const long WsExToolWindow =
        0x00000080;

    private const long WsExAppWindow =
        0x00040000;

    private const uint SwpNoSize =
        0x0001;

    private const uint SwpNoMove =
        0x0002;

    private const uint SwpNoZOrder =
        0x0004;

    private const uint SwpNoActivate =
        0x0010;

    private const uint SwpFrameChanged =
        0x0020;

    public OverlayWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            IntPtr handle =
                new WindowInteropHelper(this).Handle;

            SetWindowDisplayAffinity(
                handle,
                0x00000011);

            ExcludeFromAltTab(handle);
        };

        Loaded += (_, _) =>
        {
            Left =
                SystemParameters.WorkArea.Left + 18;

            Top =
                SystemParameters.WorkArea.Top + 18;
        };
    }

    private static void ExcludeFromAltTab(
        IntPtr windowHandle)
    {
        nint currentStyle =
            GetWindowLongPtr(
                windowHandle,
                GwlExStyle);

        long styleValue =
            currentStyle.ToInt64();

        styleValue |=
            WsExToolWindow;

        styleValue &=
            ~WsExAppWindow;

        SetWindowLongPtr(
            windowHandle,
            GwlExStyle,
            new nint(styleValue));

        SetWindowPos(
            windowHandle,
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

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        IntPtr windowHandle,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongPtrW")]
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr windowHandle,
        uint affinity);
}