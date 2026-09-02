using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;

namespace Jane.Windows.Gpu;

/// <summary>
/// The two window-side game signals: the shell's own notification state, and whether the
/// foreground window covers its monitor.
/// </summary>
/// <remarks>
/// Both are needed, and neither is sufficient. <c>SHQueryUserNotificationState</c> only reports
/// D3D *exclusive* fullscreen; the overwhelming majority of modern games run
/// borderless-windowed, where it returns <c>QUNS_ACCEPTS_NOTIFICATIONS</c> exactly as it does for
/// an idle desktop. The geometry check catches those, at the cost of also firing for a maximised
/// video player -- which is why the third signal, NVML, is what distinguishes "something is
/// fullscreen" from "something is hammering the GPU".
/// </remarks>
public sealed partial class FullscreenDetector
{
    /// <summary>
    /// Reads both window-side signals. Never throws: a signal that cannot be read is simply
    /// absent, and the routing policy treats absence as "no evidence", not as evidence.
    /// </summary>
    public (GameSignals Signals, string? ForegroundProcess) Detect()
    {
        var signals = GameSignals.None;

        if (IsShellReportingFullscreen())
        {
            signals |= GameSignals.NotificationState;
        }

        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return (signals, null);
        }

        if (CoversItsMonitor(foreground))
        {
            signals |= GameSignals.FullscreenGeometry;
        }

        return (signals, ProcessNameOf(foreground));
    }

    private static bool IsShellReportingFullscreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0)
            {
                return false;
            }

            // RunningD3dFullScreen is the game case. PresentationMode is a presentation or a
            // full-screen video; both are cases where the user equally does not want Jane loading
            // a model onto the GPU behind them.
            return state is UserNotificationState.RunningD3dFullScreen or UserNotificationState.PresentationMode;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the window's rectangle is at least as large as the monitor it is on.
    /// </summary>
    /// <remarks>
    /// Compared against the monitor rectangle rather than the *work area*, so a borderless window
    /// that covers the taskbar counts and an ordinary maximised window does not. The comparison
    /// allows a pixel of slop: some games size themselves one pixel over to defeat exactly this
    /// check, and DPI rounding can cost a pixel honestly.
    /// </remarks>
    private static bool CoversItsMonitor(nint window)
    {
        if (!GetWindowRect(window, out var bounds))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        const int Slop = 1;
        return bounds.Left <= info.rcMonitor.Left + Slop
               && bounds.Top <= info.rcMonitor.Top + Slop
               && bounds.Right >= info.rcMonitor.Right - Slop
               && bounds.Bottom >= info.rcMonitor.Bottom - Slop;
    }

    /// <summary>
    /// Names the foreground process for the routing report. Diagnostic only -- Jane never routes
    /// on a process name, because a blocklist of game executables is a list that is always wrong.
    /// </summary>
    private static string? ProcessNameOf(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out UserNotificationState state);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }
}
