using System.Windows;
using Jane.App.Overlay;
using Jane.Core.Abstractions;
using Jane.Core.Pipeline;
using Jane.Core.Settings;

namespace Jane.App.Tests;

/// <summary>
/// The resting pill, and the live level that drives its waveform.
/// </summary>
/// <remarks>
/// <para>
/// Jane's overlay originally appeared only while the hotkey was held, which leaves a background
/// app with no resting presence at all: nothing on screen says Jane is running, which key it is
/// listening for, or that it has been paused. Aqua keeps a bar visible the whole time, and it is
/// the right call -- a push-to-talk app that is invisible until you already know how to use it
/// teaches nobody anything.
/// </para>
/// <para>
/// The second half of this file covers a bug rather than a feature. <see cref="WaveformControl"/>
/// and its 30 fps pump were both built and both worked; nothing ever pushed a level into them
/// after the first frame. <see cref="OverlayWindow.Apply"/> updates its level on every call and
/// was documented as being called "about thirty times a second", but its only caller was the
/// pipeline's state-changed event, which fires once per state. So the bars were drawn once at
/// whatever the level happened to be at key-down -- near silence -- and then decayed to a flat
/// row of dots and stayed there for the whole dictation.
/// </para>
/// </remarks>
public sealed class IdleOverlayTests
{
    private static readonly HotkeyBinding RightControl = HotkeyBinding.Default;

    // ------------------------------------------------------------------ resting state

    [Fact]
    public void AnIdlePipelineShowsTheRestingPillNamingTheHotkey()
    {
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(), RightControl, paused: false);

        Assert.Equal(OverlayState.Ready, status.State);
        Assert.Contains("Right Ctrl", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestingPillNamesWhicheverKeyIsActuallyBound()
    {
        // The whole point of showing it: somebody who rebound the key can read what they bound.
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(), new HotkeyBinding(0xA5, []), paused: false);

        Assert.Contains("Right Alt", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheRestingPillOffLeavesNothingOnScreen()
    {
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(ShowWhenIdle: false), RightControl, paused: false);

        Assert.Equal(OverlayState.Idle, status.State);
    }

    [Fact]
    public void HidingTheOverlayEntirelyOutranksTheRestingPill()
    {
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(Visible: false), RightControl, paused: false);

        Assert.Equal(OverlayState.Idle, status.State);
    }

    [Fact]
    public void PausedSaysSoRatherThanInvitingAKeyPressThatDoesNothing()
    {
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(), RightControl, paused: true);

        Assert.Equal(OverlayState.Ready, status.State);
        Assert.Contains("paused", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OverlayState.Listening)]
    [InlineData(OverlayState.Thinking)]
    [InlineData(OverlayState.Injecting)]
    [InlineData(OverlayState.Error)]
    public void AnythingActuallyHappeningPassesStraightThrough(OverlayState state)
    {
        var live = new OverlayStatus(state, "whatever the pipeline said", 0.4f);

        var status = IdleOverlay.For(live, new OverlaySettings(), RightControl, paused: false);

        Assert.Same(live, status);
    }

    [Fact]
    public void TheRestingPillGetsOutOfTheWayOfAFullscreenApp()
    {
        // The pill is topmost, and it now sits there permanently rather than for the second and a
        // half of a dictation. Over a borderless-fullscreen game that is not a status indicator,
        // it is something drawn on top of what the user is doing.
        var status = IdleOverlay.For(
            OverlayStatus.Idle, new OverlaySettings(), RightControl, paused: false, fullscreen: true);

        Assert.Equal(OverlayState.Idle, status.State);
    }

    [Fact]
    public void ADictationStillShowsItsPillOverAFullscreenApp()
    {
        // Only the resting state yields. Somebody who presses the hotkey during a game has asked
        // for something to happen and needs to see whether it did.
        var live = new OverlayStatus(OverlayState.Listening, "Listening", 0.4f);

        var status = IdleOverlay.For(
            live, new OverlaySettings(), RightControl, paused: false, fullscreen: true);

        Assert.Same(live, status);
    }

    [Fact]
    public void TheRestingPillStaysOnScreenWhereIdleWouldHaveHiddenTheWindow()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var window = new OverlayWindow();
            try
            {
                window.Apply(new OverlayStatus(OverlayState.Ready, "Hold Right Ctrl to dictate"));
                sta.Settle();
                Assert.True(window.IsVisible);

                window.Apply(OverlayStatus.Idle);
                sta.Settle(250);
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheRestingPillIsDimmerThanADictationInFlight()
    {
        // It sits on screen permanently, over whatever the user is actually doing. A resting
        // indicator at full strength is not an indicator, it is clutter.
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var window = new OverlayWindow();
            try
            {
                window.Apply(new OverlayStatus(OverlayState.Ready, "Hold Right Ctrl to dictate"));
                sta.Settle(400);
                var resting = window.TargetOpacity;
                var restingDrawn = window.DrawnOpacity;

                window.Apply(new OverlayStatus(OverlayState.Listening, "Listening"));
                sta.Settle(400);

                Assert.True(resting < window.TargetOpacity, $"resting target {resting} was not dimmer");
                Assert.True(
                    restingDrawn < window.DrawnOpacity,
                    $"resting pill drew at {restingDrawn}, active at {window.DrawnOpacity}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheRestingPillIsStillInvisibleToTheKeyboard()
    {
        // Same requirement as every other state, restated because this one is on screen all day
        // rather than for the second and a half somebody holds a key.
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var window = new OverlayWindow();
            try
            {
                window.Apply(new OverlayStatus(OverlayState.Ready, "Hold Right Ctrl to dictate"));
                sta.Settle();

                Assert.False(window.Focusable);
                Assert.False(window.ShowActivated);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ placement

    [Fact]
    public void BottomCentrePlacesThePillCentredAboveTheWorkAreaFloor()
    {
        var placed = OverlayPlacement.BottomCentre(
            new PixelRect(0, 0, 2560, 1400), windowWidth: 200, windowHeight: 60, margin: 4);

        Assert.Equal((2560 - 200) / 2, placed.X);
        Assert.Equal(1400 - 60 - 4, placed.Y);
    }

    [Fact]
    public void BottomCentreStaysOnTheMonitorThatOwnsTheWorkArea()
    {
        // A second monitor to the left has negative coordinates on the virtual desktop, and a
        // placement that assumed the primary screen's origin would put the pill on the wrong one.
        var placed = OverlayPlacement.BottomCentre(
            new PixelRect(-1920, 0, 0, 1080), windowWidth: 200, windowHeight: 60, margin: 4);

        Assert.InRange(placed.X, -1920, -200);
        Assert.InRange(placed.Y, 0, 1080 - 60);
    }

    [Fact]
    public void BottomCentreKeepsAnOverwidePillOnScreenAtItsLeftEdge()
    {
        // A long error message on a small display is wider than the work area. Overflowing the
        // right edge beats overflowing the left, where the start of the sentence lives.
        var placed = OverlayPlacement.BottomCentre(
            new PixelRect(0, 0, 800, 600), windowWidth: 1200, windowHeight: 60, margin: 4);

        Assert.Equal(0, placed.X);
    }

    // ------------------------------------------------------------------ live level

    [Fact]
    public async Task TheOverlayIsGivenAFreshLevelManyTimesDuringOneDictation()
    {
        // The regression test for the flat waveform. One Show per pipeline state is what shipped;
        // a waveform needs a stream of them.
        var overlay = new RecordingPresenter();
        var level = 0f;

        using var pump = new OverlayLevelPump(overlay, () => level, TimeSpan.FromMilliseconds(15));

        Enter(overlay, pump, PipelineState.Listening);
        for (var i = 0; i < 10; i++)
        {
            level = 0.1f + (i * 0.05f);
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        Enter(overlay, pump, PipelineState.Transcribing);

        var levels = overlay.Levels.Distinct().ToList();
        Assert.True(levels.Count > 3, $"only {levels.Count} distinct levels reached the overlay");
    }

    [Fact]
    public async Task TheLevelPumpStopsWhenTheDictationDoes()
    {
        // A pump left running is a timer waking the UI thread thirty times a second forever, on
        // an app whose whole idle budget is a fraction of one core.
        var overlay = new RecordingPresenter();

        using var pump = new OverlayLevelPump(overlay, () => 0.5f, TimeSpan.FromMilliseconds(15));

        Enter(overlay, pump, PipelineState.Listening);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Enter(overlay, pump, PipelineState.Idle);

        var afterStop = overlay.Shows;
        await Task.Delay(90, TestContext.Current.CancellationToken);

        Assert.Equal(afterStop, overlay.Shows);
    }

    [Fact]
    public async Task TheLevelPumpNeverOverwritesAStateItDoesNotOwn()
    {
        // It reaches into the presenter's current status and republishes it with a new level.
        // Doing that after the pipeline has moved on would resurrect "Listening" over the top of
        // "Transcribing" and leave the pill lying about what Jane is doing.
        var overlay = new RecordingPresenter();

        using var pump = new OverlayLevelPump(overlay, () => 0.5f, TimeSpan.FromMilliseconds(15));

        Enter(overlay, pump, PipelineState.Listening);
        await Task.Delay(45, TestContext.Current.CancellationToken);

        Assert.All(overlay.States, state => Assert.Equal(OverlayState.Listening, state));
    }

    /// <summary>
    /// Moves the pipeline to a state, in the order <c>JaneHost</c> does it.
    /// </summary>
    /// <remarks>
    /// The status is published first and the pump told second, which is the pump's contract: it
    /// republishes a status the presenter already holds rather than inventing one, so that it can
    /// never resurrect "Listening" over the top of whatever came next. Driving the pump without
    /// publishing first would be testing an arrangement production never produces.
    /// </remarks>
    private static void Enter(RecordingPresenter overlay, OverlayLevelPump pump, PipelineState state)
    {
        var status = new PipelineStatus(state);
        overlay.Show(status.ToOverlayStatus());
        pump.OnState(status);
    }

    private sealed class RecordingPresenter : IOverlayPresenter
    {
        private readonly Lock _gate = new();
        private readonly List<float> _levels = [];
        private readonly List<OverlayState> _states = [];

        public OverlayStatus Status { get; private set; } = OverlayStatus.Idle;

        public int Shows
        {
            get
            {
                lock (_gate)
                {
                    return _levels.Count;
                }
            }
        }

        public IReadOnlyList<float> Levels
        {
            get
            {
                lock (_gate)
                {
                    return [.. _levels];
                }
            }
        }

        public IReadOnlyList<OverlayState> States
        {
            get
            {
                lock (_gate)
                {
                    return [.. _states];
                }
            }
        }

        public void Show(OverlayStatus status)
        {
            lock (_gate)
            {
                Status = status;
                _levels.Add(status.Level);
                _states.Add(status.State);
            }
        }

        public void Hide() => Show(OverlayStatus.Idle);
    }
}
