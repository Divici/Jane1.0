using Jane.App.Overlay;
using Jane.Core.Abstractions;

namespace Jane.App.Tests;

/// <summary>
/// How much the resting pill says when nothing is happening.
/// </summary>
/// <remarks>
/// <para>
/// The resting pill was built to name the hotkey, because a background app that is invisible until
/// you already know how to use it teaches nobody anything. That is right the first week and wrong
/// forever after: "Hold Right Ctrl to dictate" is a sentence sitting over your work all day, long
/// after you have learned it.
/// </para>
/// <para>
/// So the text is earned rather than given. At rest the pill is a mark and nothing else; point at
/// it and it says what it is for. The message itself is unchanged -- it is still computed, still
/// the accessible name, and still what a screen reader reads -- only its drawing waits for the
/// pointer.
/// </para>
/// <para>
/// The pointer test cannot come from WPF. The overlay is <c>WS_EX_TRANSPARENT</c> and
/// <c>IsHitTestVisible="False"</c> so that clicks reach the window underneath, which also means it
/// receives no mouse events at all. Removing either would put a dead rectangle over whatever the
/// user is working on, so hover is decided by asking where the cursor is and comparing -- which is
/// this pure function, and why it is testable without a window.
/// </para>
/// </remarks>
public sealed class RestingPillTests
{
    /// <summary>Left 100, top 200, 240 wide and 40 tall -- right and bottom are exclusive.</summary>
    private static readonly PixelRect Pill = new(100, 200, 340, 240);

    [Theory]
    [InlineData(220, 220)] // dead centre
    [InlineData(100, 200)] // top-left corner, inclusive
    [InlineData(339, 239)] // bottom-right corner, inclusive
    public void ThePointerIsOverThePillWhenItIsInsideIt(int x, int y)
    {
        Assert.True(RestingPill.Covers(Pill, x, y));
    }

    [Theory]
    [InlineData(99, 220)]  // one pixel left
    [InlineData(340, 220)] // one pixel right
    [InlineData(220, 199)] // one pixel above
    [InlineData(220, 240)] // one pixel below
    [InlineData(0, 0)]
    public void ThePointerIsNotOverThePillWhenItIsOutsideIt(int x, int y)
    {
        Assert.False(RestingPill.Covers(Pill, x, y));
    }

    [Fact]
    public void AnEmptyRectangleCoversNothing()
    {
        // The pill has no size before its first layout pass. Treating that as "the pointer is
        // inside it" would flash the text on every start-up.
        Assert.False(RestingPill.Covers(new PixelRect(0, 0, 0, 0), 0, 0));
    }

    [Fact]
    public void TheRestingPillIsSilentUntilPointedAt()
    {
        var resting = new OverlayStatus(OverlayState.Ready, "Hold Right Ctrl to dictate");

        Assert.False(RestingPill.ShowsText(resting, hovered: false));
        Assert.True(RestingPill.ShowsText(resting, hovered: true));
    }

    [Theory]
    [InlineData(OverlayState.Connecting)]
    [InlineData(OverlayState.Listening)]
    [InlineData(OverlayState.Thinking)]
    [InlineData(OverlayState.Injecting)]
    [InlineData(OverlayState.EditModeUnavailable)]
    [InlineData(OverlayState.Error)]
    public void EveryOtherStateAlwaysSpeaks(OverlayState state)
    {
        // Only the resting pill is decoration. Once something is happening the pill is the only
        // report of it, and hiding that behind a hover would mean an error nobody ever sees.
        Assert.True(RestingPill.ShowsText(new OverlayStatus(state, "something"), hovered: false));
    }

    [Fact]
    public void PausedIsStillEarnedRatherThanGiven()
    {
        // Deliberate. Paused is worth surfacing -- it explains why the key does nothing -- but the
        // ask was a blank pill at rest, and a permanent "Jane is paused" is the same sentence over
        // the same work. The dot carries it instead; the words arrive on hover.
        var paused = new OverlayStatus(OverlayState.Ready, "Jane is paused");

        Assert.False(RestingPill.ShowsText(paused, hovered: false));
        Assert.True(RestingPill.ShowsText(paused, hovered: true));
    }

    [Fact]
    public void AMessagelessRestingPillStaysSilentEvenWhenPointedAt()
    {
        // Nothing to reveal, so hovering must not expand the pill around an empty string.
        Assert.False(RestingPill.ShowsText(new OverlayStatus(OverlayState.Ready), hovered: true));
    }
}
