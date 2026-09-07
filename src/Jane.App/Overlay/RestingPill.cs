using Jane.Core.Abstractions;

namespace Jane.App.Overlay;

/// <summary>
/// Whether the pill says anything while nothing is happening.
/// </summary>
/// <remarks>
/// <para>
/// The resting pill exists so a background app is not invisible until you already know how to use
/// it. Naming the hotkey does that job on the first day and keeps doing it forever after, which is
/// the problem: "Hold Right Ctrl to dictate" ends up a sentence parked over your work all day, long
/// after it has taught you anything.
/// </para>
/// <para>
/// So the text is earned. At rest the pill is a mark; point at it and it explains itself. The
/// message is still computed and still the accessible name -- a screen reader is not made to hover
/// -- only its drawing waits for the pointer.
/// </para>
/// <para>
/// Hover cannot come from WPF here. The overlay carries <c>WS_EX_TRANSPARENT</c> and
/// <c>IsHitTestVisible="False"</c> precisely so clicks reach the window underneath, and a window
/// that takes no mouse input raises no mouse events. Dropping either flag would leave a dead
/// rectangle sitting over whatever the user is working on -- a worse bug than the one being fixed
/// -- so the pointer is located and compared instead. That comparison is this class, kept apart
/// from the window so it can be argued with in a test rather than by waving a mouse at a screen.
/// </para>
/// </remarks>
public static class RestingPill
{
    /// <summary>
    /// Whether a screen point falls inside the pill.
    /// </summary>
    /// <remarks>
    /// Right and bottom are exclusive, matching the Win32 <c>RECT</c> the bounds come from: a
    /// window at left 100 and right 340 owns columns 100 to 339, and column 340 belongs to
    /// whatever is next to it.
    /// </remarks>
    public static bool Covers(PixelRect pill, int x, int y)
    {
        // A pill that has not been laid out yet has no area. Without this it would contain the
        // origin, and a cursor parked in the top-left corner would reveal text on every start-up.
        if (pill.Width <= 0 || pill.Height <= 0)
        {
            return false;
        }

        return x >= pill.Left && x < pill.Right && y >= pill.Top && y < pill.Bottom;
    }

    /// <summary>
    /// Whether the pill should draw its message.
    /// </summary>
    /// <param name="status">What the presenter is showing.</param>
    /// <param name="hovered">Whether the pointer is over the pill.</param>
    public static bool ShowsText(OverlayStatus status, bool hovered)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (string.IsNullOrWhiteSpace(status.Message))
        {
            return false;
        }

        // Only the resting pill is decoration. Once a dictation is under way the pill is the sole
        // report of it, and an error that waits to be hovered over is an error nobody reads.
        return status.State != OverlayState.Ready || hovered;
    }
}
