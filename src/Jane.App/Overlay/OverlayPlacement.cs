namespace Jane.App.Overlay;

/// <summary>A rectangle in physical pixels, as Win32 reports one.</summary>
/// <remarks>
/// Physical pixels, not WPF's device-independent units, are the only coordinate space that is
/// unambiguous across monitors: a second display at 150% scale has its own DIP-to-pixel ratio,
/// and <c>Window.Left</c>/<c>Top</c> are interpreted against the window's *current* monitor. So
/// placement is computed in pixels and applied with <c>SetWindowPos</c>, and WPF's per-monitor
/// DPI handling then rescales the content once the window lands.
/// </remarks>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Top-left corner of the overlay, in physical pixels on the virtual desktop.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>Which screen edge the taskbar is docked to.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Where the pill sits. Tray-relative in this phase.
/// </summary>
/// <remarks>
/// Caret-relative placement is Phase 8's, where the UIA machinery that can locate a caret
/// actually exists. Until then the pill anchors to the notification area, which is where the
/// user's eye already goes for a background app's status, and which needs nothing but window
/// geometry to find.
/// <para>
/// Everything here is pure arithmetic on rectangles the caller supplies, so multi-monitor and
/// per-monitor-DPI behaviour is testable without a second screen: the work area of the monitor
/// holding the tray is passed in, never read from <c>SystemParameters.PrimaryScreen*</c>, which
/// would put the pill on the wrong display the moment the taskbar is not on the primary one.
/// </para>
/// </remarks>
public static class OverlayPlacement
{
    /// <summary>
    /// Gap between the *window bounds* and the work-area edge, in device-independent units.
    /// </summary>
    /// <remarks>
    /// Small on purpose: the overlay window carries 14 DIP of transparent padding around the
    /// pill so its drop shadow is not clipped, so the visible gap is this plus that padding.
    /// </remarks>
    public const double MarginDip = 4;

    /// <summary>
    /// Infers the taskbar edge from the tray window's own rectangle.
    /// </summary>
    /// <remarks>
    /// The taskbar spans the full length of the edge it is docked to, so its longer axis names
    /// its orientation, and the edge it hugs names the side. Nothing here assumes "bottom".
    /// </remarks>
    public static TaskbarEdge EdgeOf(PixelRect monitor, PixelRect tray)
    {
        if (tray.Width >= tray.Height)
        {
            return tray.Top - monitor.Top <= monitor.Bottom - tray.Bottom
                ? TaskbarEdge.Top
                : TaskbarEdge.Bottom;
        }

        return tray.Left - monitor.Left <= monitor.Right - tray.Right
            ? TaskbarEdge.Left
            : TaskbarEdge.Right;
    }

    /// <summary>
    /// Places the pill in the work-area corner nearest the notification area.
    /// </summary>
    /// <param name="workArea">
    /// The work area -- not the monitor bounds -- of the monitor holding the tray, so the pill
    /// never lands underneath the taskbar or an app bar.
    /// </param>
    /// <param name="margin">Gap from the work-area edges, in physical pixels for that monitor.</param>
    public static PixelPoint Calculate(
        PixelRect workArea,
        TaskbarEdge edge,
        int windowWidth,
        int windowHeight,
        int margin)
    {
        var x = edge == TaskbarEdge.Left
            ? workArea.Left + margin
            : workArea.Right - margin - windowWidth;

        var y = edge == TaskbarEdge.Top
            ? workArea.Top + margin
            : workArea.Bottom - margin - windowHeight;

        return new PixelPoint(
            Clamp(x, workArea.Left, workArea.Right - windowWidth),
            Clamp(y, workArea.Top, workArea.Bottom - windowHeight));
    }

    /// <summary>
    /// Places the pill centred along the bottom of the work area.
    /// </summary>
    /// <remarks>
    /// The default, and where a push-to-talk indicator belongs: it sits on the path between the
    /// keyboard and the thing being dictated into, rather than in the corner where a background
    /// app's status usually hides. The work area already excludes the taskbar, so "the bottom of
    /// the screen" means just above it wherever it happens to be docked -- and on a machine with
    /// the taskbar on the left or right, this still reads as the bottom edge.
    /// </remarks>
    /// <param name="workArea">
    /// The work area of the monitor the pill belongs on, in physical pixels on the virtual
    /// desktop. Never <c>SystemParameters.PrimaryScreen*</c>: a second monitor to the left has
    /// negative coordinates, and assuming an origin of zero puts the pill on the wrong display.
    /// </param>
    /// <param name="margin">Gap from the work-area floor, in physical pixels for that monitor.</param>
    public static PixelPoint BottomCentre(
        PixelRect workArea,
        int windowWidth,
        int windowHeight,
        int margin)
    {
        var x = workArea.Left + ((workArea.Width - windowWidth) / 2);
        var y = workArea.Bottom - margin - windowHeight;

        return new PixelPoint(
            Clamp(x, workArea.Left, workArea.Right - windowWidth),
            Clamp(y, workArea.Top, workArea.Bottom - windowHeight));
    }

    // A long error message on a small display can be wider than the work area, which inverts the
    // clamp range. Starting on-screen and overflowing the far edge beats the reverse.
    private static int Clamp(int value, int min, int max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
