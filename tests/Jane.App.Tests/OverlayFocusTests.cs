using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Jane.App.Overlay;
using Jane.Core.Abstractions;

namespace Jane.App.Tests;

/// <summary>
/// The overlay's one hard requirement, asserted directly.
/// </summary>
/// <remarks>
/// Jane types into whatever window the user was already using. A moment of stolen focus does not
/// just look wrong -- it sends the dictation into Jane's own pill instead of the document, and
/// the text is gone. Every other overlay defect is cosmetic next to this one.
/// </remarks>
public sealed class OverlayFocusTests
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExNoActivate = 0x0800_0000;

    [Fact]
    public void ForegroundWindowNeverMovesAcrossEveryStateTransition()
    {
        using var sta = new StaTestContext();

        // A window of our own is the ideal foreground holder, but SetForegroundWindow is subject
        // to Windows' foreground lock and can be refused. That is fine: whatever ends up holding
        // foreground is the baseline, and the assertion -- "it did not move" -- is the same.
        var holder = sta.Invoke(() =>
        {
            var window = new Window
            {
                Title = "Jane overlay focus baseline",
                Width = 420,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            window.Show();
            window.Activate();
            return window;
        });

        sta.Settle(150);

        var baseline = GetForegroundWindow();
        Assert.SkipWhen(
            baseline == IntPtr.Zero,
            "No window holds foreground, so this session has no interactive desktop to steal focus from.");

        using var presenter = new OverlayPresenter(sta.Dispatcher);
        var overlayHandle = sta.Invoke(() => new WindowInteropHelper(presenter.Window).Handle);

        Assert.NotEqual(IntPtr.Zero, overlayHandle);
        Assert.NotEqual(overlayHandle, baseline);

        foreach (var state in Enum.GetValues<OverlayState>())
        {
            presenter.Show(Status(state));
            sta.Settle();
            AssertForegroundUnmoved(baseline, overlayHandle, $"after showing {state}");

            presenter.Hide();
            sta.Settle();
            AssertForegroundUnmoved(baseline, overlayHandle, $"after hiding from {state}");
        }

        // Re-showing after a hide walks the HWND through a second show; a window that only
        // behaves on its first appearance would pass everything above.
        presenter.Show(new OverlayStatus(OverlayState.Listening, "Listening", 0.9f));
        sta.Settle();
        AssertForegroundUnmoved(baseline, overlayHandle, "after re-showing following a hide");

        sta.Invoke(holder.Close);
    }

    [Fact]
    public void ExtendedStylesAreAppliedBeforeTheWindowIsEverShown()
    {
        using var sta = new StaTestContext();

        // WPF creates the HWND lazily. If the styles were applied on first Show there would be a
        // window -- literally -- between the HWND appearing and WS_EX_NOACTIVATE landing on it,
        // and a click or a race in that gap takes focus.
        var handle = sta.Invoke(() =>
        {
            var window = new OverlayWindow();
            return new WindowInteropHelper(window).Handle;
        });

        Assert.NotEqual(IntPtr.Zero, handle);

        var exStyle = (int)GetWindowLongPtr(handle, GwlExStyle);

        Assert.Equal(WsExNoActivate, exStyle & WsExNoActivate);
        Assert.Equal(WsExToolWindow, exStyle & WsExToolWindow);
        Assert.Equal(WsExTransparent, exStyle & WsExTransparent);
    }

    [Fact]
    public void WindowRefusesActivationAndFocusByConstruction()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var window = new OverlayWindow();

            // ShowActivated and Focusable are the managed half of the same promise the extended
            // styles make natively. Both halves are needed: WPF routes keyboard focus itself.
            Assert.False(window.ShowActivated);
            Assert.False(window.Focusable);
            Assert.False(window.IsHitTestVisible);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.Topmost);
        });
    }

    [Fact]
    public void IdleStatusLeavesNothingOnScreen()
    {
        using var sta = new StaTestContext();
        using var presenter = new OverlayPresenter(sta.Dispatcher);

        presenter.Show(new OverlayStatus(OverlayState.Listening, "Listening"));
        sta.Settle();
        Assert.True(sta.Invoke(() => presenter.Window.IsVisible));

        // Idle is documented as "hidden entirely -- not a transparent window", so the HWND must
        // actually come down rather than linger at zero opacity eating a compositor layer.
        presenter.Show(OverlayStatus.Idle);
        sta.Settle();
        Assert.False(sta.Invoke(() => presenter.Window.IsVisible));
        Assert.Equal(OverlayState.Idle, presenter.Status.State);
    }

    [Fact]
    public void EveryStateExposesAnAccessibleName()
    {
        using var sta = new StaTestContext();
        using var presenter = new OverlayPresenter(sta.Dispatcher);

        // The pill is the only feedback a screen-reader user gets; a state with no text
        // equivalent is a state they cannot observe at all.
        foreach (var state in Enum.GetValues<OverlayState>())
        {
            presenter.Show(Status(state));
            sta.Settle(10);

            var name = sta.Invoke(() => presenter.Window.AccessibleStatusText);
            Assert.False(string.IsNullOrWhiteSpace(name), $"{state} has no accessible text.");
        }
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom)]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void PlacementStaysInsideTheWorkAreaOfTheTraysMonitor(TaskbarEdge edge)
    {
        // A secondary monitor left of the primary has negative coordinates, which is exactly
        // where SystemParameters.PrimaryScreen* would have put the pill off-screen.
        var workArea = new PixelRect(-2560, 120, -160, 1320);

        var point = OverlayPlacement.Calculate(workArea, edge, windowWidth: 260, windowHeight: 34, margin: 16);

        Assert.InRange(point.X, workArea.Left, workArea.Right - 260);
        Assert.InRange(point.Y, workArea.Top, workArea.Bottom - 34);
    }

    [Fact]
    public void PlacementHugsTheCornerNearestTheNotificationArea()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        var bottom = OverlayPlacement.Calculate(workArea, TaskbarEdge.Bottom, 300, 40, 16);
        var top = OverlayPlacement.Calculate(workArea, TaskbarEdge.Top, 300, 40, 16);
        var left = OverlayPlacement.Calculate(workArea, TaskbarEdge.Left, 300, 40, 16);

        Assert.Equal(new PixelPoint(1920 - 16 - 300, 1040 - 16 - 40), bottom);
        Assert.Equal(new PixelPoint(1920 - 16 - 300, 16), top);
        Assert.Equal(new PixelPoint(16, 1040 - 16 - 40), left);
    }

    [Fact]
    public void PlacementClampsAWindowWiderThanTheWorkArea()
    {
        // A very long error message on a small monitor must still start on-screen.
        var workArea = new PixelRect(0, 0, 800, 600);

        var point = OverlayPlacement.Calculate(workArea, TaskbarEdge.Bottom, windowWidth: 1200, windowHeight: 40, margin: 16);

        Assert.Equal(0, point.X);
    }

    [Fact]
    public void TaskbarEdgeIsDerivedFromTheTrayRectangleNotAssumedToBeTheBottom()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);

        Assert.Equal(TaskbarEdge.Bottom, OverlayPlacement.EdgeOf(monitor, new PixelRect(0, 1040, 1920, 1080)));
        Assert.Equal(TaskbarEdge.Top, OverlayPlacement.EdgeOf(monitor, new PixelRect(0, 0, 1920, 40)));
        Assert.Equal(TaskbarEdge.Left, OverlayPlacement.EdgeOf(monitor, new PixelRect(0, 0, 62, 1080)));
        Assert.Equal(TaskbarEdge.Right, OverlayPlacement.EdgeOf(monitor, new PixelRect(1858, 0, 1920, 1080)));
    }

    private static OverlayStatus Status(OverlayState state) => state switch
    {
        OverlayState.Idle => OverlayStatus.Idle,
        OverlayState.Listening => new OverlayStatus(state, "Listening", 0.42f),
        OverlayState.Thinking => new OverlayStatus(state),
        OverlayState.Injecting => new OverlayStatus(state),
        OverlayState.EditModeUnavailable => new OverlayStatus(state),
        OverlayState.Error => new OverlayStatus(state, "Microphone unavailable"),
        _ => OverlayStatus.Idle,
    };

    private static void AssertForegroundUnmoved(IntPtr baseline, IntPtr overlay, string when)
    {
        var current = GetForegroundWindow();

        Assert.False(current == overlay, $"The overlay took foreground {when}.");
        Assert.True(
            current == baseline,
            $"Foreground moved from 0x{baseline:X} to 0x{current:X} {when}.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
}
