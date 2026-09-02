using System.Runtime.InteropServices;

namespace Jane.App.Overlay;

/// <summary>
/// The Win32 surface the overlay needs: the extended styles that stop it taking focus, and the
/// monitor geometry that decides where it sits.
/// </summary>
/// <remarks>
/// WPF cannot express any of this. <c>ShowActivated="False"</c> stops WPF calling
/// <c>SetForegroundWindow</c>, but the HWND is still activatable by a click, an
/// <c>Alt+Tab</c>, or a shell that decides to raise it -- only <c>WS_EX_NOACTIVATE</c> closes
/// that off. And WPF's <c>Left</c>/<c>Top</c> are device-independent units resolved against the
/// window's current monitor, which is the wrong space for a window that has to land on a
/// specific pixel of a possibly differently-scaled screen.
/// </remarks>
internal static partial class OverlayInterop
{
    /// <summary>Sent when the user clicks a window that is not active. Answered with MA_NOACTIVATE.</summary>
    internal const int WmMouseActivate = 0x0021;

    /// <summary>"Do not activate, and do not pass the click on."</summary>
    internal const int MaNoActivate = 3;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExNoActivate = 0x0800_0000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private const uint MonitorDefaultToPrimary = 0x0000_0001;

    /// <summary>Windows 11's taskbar still uses the class name it has had since Windows 95.</summary>
    private const string TaskbarClass = "Shell_TrayWnd";

    /// <summary>
    /// Adds the three extended styles that make the window unfocusable, invisible to Alt+Tab, and
    /// click-through. Called from <c>OnSourceInitialized</c>, before the window is ever shown.
    /// </summary>
    /// <remarks>
    /// The styles are OR-ed in, never assigned: <c>AllowsTransparency</c> has already put
    /// <c>WS_EX_LAYERED</c> on this HWND, and overwriting it would leave a black rectangle where
    /// the rounded pill should be.
    /// </remarks>
    internal static void MakeNonActivating(nint handle)
    {
        var current = GetWindowLongPtr(handle, GwlExStyle);
        var wanted = current | WsExNoActivate | WsExToolWindow | WsExTransparent;

        if (wanted != current)
        {
            _ = SetWindowLongPtr(handle, GwlExStyle, wanted);
        }
    }

    internal static PixelRect WindowRect(nint handle) =>
        GetWindowRect(handle, out var rect) ? ToPixelRect(rect) : default;

    /// <summary>Physical pixels per device-independent unit for the monitor this window is on.</summary>
    internal static double DpiScale(nint handle)
    {
        var dpi = GetDpiForWindow(handle);

        // GetDpiForWindow returns 0 for a handle Windows does not recognise; 96 is the identity.
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    internal static void MoveWithoutActivating(nint handle, PixelPoint point) =>
        _ = SetWindowPos(handle, 0, point.X, point.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>
    /// Finds the work area and taskbar edge of the monitor that holds the notification area.
    /// </summary>
    /// <remarks>
    /// Deliberately anchored to the taskbar's monitor rather than the primary one. On this
    /// machine they are the same; on a laptop docked to an external screen they routinely are
    /// not, and the pill would otherwise appear on a display the user is not looking at.
    /// </remarks>
    internal static bool TryResolveTrayAnchor(out PixelRect workArea, out TaskbarEdge edge)
    {
        workArea = default;
        edge = TaskbarEdge.Bottom;

        var trayNative = default(NativeRect);
        var taskbar = FindWindow(TaskbarClass, null);
        var haveTrayRect = taskbar != 0 && GetWindowRect(taskbar, out trayNative);

        var monitor = haveTrayRect
            ? MonitorFromWindow(taskbar, MonitorDefaultToPrimary)
            : MonitorFromPoint(default, MonitorDefaultToPrimary);

        if (monitor == 0)
        {
            return false;
        }

        var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = ToPixelRect(info.Work);

        // With no taskbar to measure -- a kiosk shell, or the explorer restart window -- the
        // bottom edge is the safe default, and the work area alone still places the pill legally.
        edge = haveTrayRect
            ? OverlayPlacement.EdgeOf(ToPixelRect(info.Monitor), ToPixelRect(trayNative))
            : TaskbarEdge.Bottom;

        return workArea.Width > 0 && workArea.Height > 0;
    }

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hWnd);
}
