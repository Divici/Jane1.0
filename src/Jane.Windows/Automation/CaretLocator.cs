using System.Runtime.InteropServices;
using Jane.Core.Abstractions;

namespace Jane.Windows.Automation;

/// <summary>
/// Where the text caret is, in physical screen pixels.
/// </summary>
/// <remarks>
/// <para>
/// Width and height, not right and bottom, because that is the shape both sources produce: a
/// <c>GUITHREADINFO</c> caret is a rectangle and a UI Automation text range reports
/// <c>left, top, width, height</c>. Storing edges would mean converting twice and getting it
/// wrong once.
/// </para>
/// <para>
/// Physical pixels, not device-independent units. Both sources are physical, and the conversion
/// depends on the DPI of the monitor the caret is on, which only the presentation layer knows.
/// </para>
/// </remarks>
public readonly record struct CaretRect(double Left, double Top, double Width, double Height)
{
    /// <summary>A caret whose rectangle is degenerate carries no placement information.</summary>
    public bool IsEmpty => Width <= 0 && Height <= 0;

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    /// <summary>The point an overlay should hang from: the caret's baseline, horizontally centred.</summary>
    public double AnchorX => Left + (Width / 2);

    public double AnchorY => Top + Height;

    public static CaretRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}

/// <summary>
/// Finds the caret rectangle for the focused window, so the overlay can sit next to the text
/// instead of in a fixed corner.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, cheapest first.
/// </para>
/// <list type="number">
/// <item><description>
/// <c>GetGUIThreadInfo</c>. A plain Win32 call with no COM in it at all -- microseconds, no
/// cross-process round trip, no chance of wedging. It answers for every application that creates a
/// real system caret, which is most classic Win32, WinForms and WPF text controls.
/// </description></item>
/// <item><description>
/// The rectangle UI Automation already returned for the selection or caret range, passed in by
/// the caller. Chromium and Electron draw their own carets and create no system caret, so this is
/// the only source that answers for Chrome, VS Code and Slack -- but it costs a UIA round trip,
/// which is why it is never fetched here and is only ever reused from a read that already
/// happened.
/// </description></item>
/// </list>
/// <para>
/// When neither answers, the result is null rather than a guess. A caret-relative overlay placed
/// at coordinates nobody vouched for is worse than the fixed placement it replaced, so the caller
/// keeps its Phase 4 position.
/// </para>
/// </remarks>
public sealed partial class CaretLocator
{
    /// <summary>
    /// The caret for a window, preferring the system caret and falling back to a rectangle UI
    /// Automation already produced.
    /// </summary>
    /// <param name="uiaCaret">
    /// <c>ContextRead.Caret</c> from the read that started at the same key-down. Optional: a
    /// caller with no Deep Context read still gets the system-caret answer.
    /// </param>
    public CaretRect? Locate(TargetWindow target, CaretRect? uiaCaret = null)
    {
        if (!target.IsNone && FromSystemCaret(target.Handle) is { } system)
        {
            return system;
        }

        return uiaCaret is { IsEmpty: false } ? uiaCaret : null;
    }

    /// <summary>
    /// The system caret of the thread that owns <paramref name="window"/>, in screen pixels.
    /// </summary>
    /// <remarks>
    /// The rectangle comes back in the client coordinates of whichever window actually owns the
    /// caret, which is usually a child control rather than the top-level window that was asked
    /// about -- so the conversion has to go through <c>hwndCaret</c>, not through the argument.
    /// </remarks>
    public CaretRect? FromSystemCaret(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0)
        {
            return null;
        }

        var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == 0)
        {
            return null;
        }

        var rect = info.rcCaret;
        if (rect.Right <= rect.Left && rect.Bottom <= rect.Top)
        {
            return null;
        }

        var topLeft = new Point { X = rect.Left, Y = rect.Top };
        var bottomRight = new Point { X = rect.Right, Y = rect.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight))
        {
            return null;
        }

        // A caret with zero width is normal -- it is a blinking line. Give it one pixel so the
        // rectangle stays usable as an anchor, and never treat it as empty.
        var width = Math.Max(bottomRight.X - topLeft.X, 1);
        var height = Math.Max(bottomRight.Y - topLeft.Y, 1);
        return new CaretRect(topLeft.X, topLeft.Y, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public Rect rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint handle, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint handle, ref Point point);
}
