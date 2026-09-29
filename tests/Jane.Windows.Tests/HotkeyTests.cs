using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Windows.Hotkeys;

namespace Jane.Windows.Tests;

/// <summary>
/// Hotkey semantics, driven with synthetic key events rather than real ones.
/// </summary>
/// <remarks>
/// Nothing here installs a hook or presses a key: the hook procedure's body, the queue it writes
/// into and the state machine that reads it are separate, directly callable pieces, precisely so
/// that "does not swallow the key" and "does not allocate" can be asserted rather than assumed.
/// </remarks>
public sealed class HotkeyTests
{
    private const int RightCtrl = HotkeyBinding.VkRightControl;
    private const int LeftShift = 0xA0;
    private const int LetterK = 0x4B;

    // Raw events carry Stopwatch ticks, which is what the hook proc can read without allocating.
    private static long At(double milliseconds) =>
        (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    private static (HotkeyStateMachine Machine, List<HotkeyEvent> Events) NewMachine(
        HotkeyMode mode = HotkeyMode.Hold,
        HotkeyOptions? options = null,
        HotkeyBinding? binding = null)
    {
        var events = new List<HotkeyEvent>();
        var machine = new HotkeyStateMachine(binding ?? HotkeyBinding.Default, mode, options);
        return (machine, events);
    }

    [Fact]
    public void HookProcPerformsNoAllocation()
    {
        // Windows silently unhooks a callback that overruns LowLevelHooksTimeout, and a GC
        // pause inside the proc is the most plausible way to overrun it. So the proc enqueues a
        // struct into a pre-sized ring and returns -- no boxing, no closure, no list growth.
        using var hook = new LowLevelKeyboardHook();

        // Warm the JIT: the first call through a method allocates for tiering, not for the work.
        for (var i = 0; i < 2_000; i++)
        {
            hook.RecordHookEvent(RightCtrl, i % 2 == 0, At(i));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            hook.RecordHookEvent(RightCtrl, i % 2 == 0, At(i));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void HookDoesNotBlockTheKeyFromReachingOtherApps()
    {
        // A dictation hotkey that ate Right Ctrl would break push-to-talk in every voice chat on
        // the machine. Returning false here is what makes the real proc fall through to
        // CallNextHookEx.
        using var hook = new LowLevelKeyboardHook();

        Assert.False(hook.RecordHookEvent(RightCtrl, isKeyDown: true, At(0)));
        Assert.False(hook.RecordHookEvent(RightCtrl, isKeyDown: false, At(500)));
        Assert.False(hook.RecordHookEvent(LetterK, isKeyDown: true, At(600)));
        Assert.False(hook.RecordHookEvent(LeftShift, isKeyDown: true, At(700)));
    }

    [Fact]
    public void AKeyTheUserPressedCountsAsActivity()
    {
        // Spacing falls back to what Jane last typed, which stops being true the moment the user
        // types anything themselves.
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordHookEvent(LetterK, isKeyDown: true, At(0));

        Assert.NotEqual(before, hook.Version);
    }

    [Fact]
    public void TheDictationKeyItselfIsNotActivity()
    {
        // Every dictation begins with this key. Counting it would discard the memory every single
        // time and bring back "Hello there.How are you?".
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordHookEvent(RightCtrl, isKeyDown: true, At(0));
        hook.RecordHookEvent(RightCtrl, isKeyDown: true, At(30));
        hook.RecordHookEvent(RightCtrl, isKeyDown: false, At(900));

        Assert.Equal(before, hook.Version);
    }

    [Fact]
    public void AModifierTheBindingRequiresIsNotActivityEither()
    {
        using var hook = new LowLevelKeyboardHook(new HotkeyBinding(LetterK, [LeftShift]));
        var before = hook.Version;

        hook.RecordHookEvent(LeftShift, isKeyDown: true, At(0));
        hook.RecordHookEvent(LetterK, isKeyDown: true, At(10));

        Assert.Equal(before, hook.Version);
    }

    [Fact]
    public void JanesOwnKeystrokesAreNotActivity()
    {
        // Injection is thousands of synthetic key events. They arrive at the hook like any other,
        // flagged as injected, and they are the one input Jane already knows about.
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordHookEvent(LetterK, isKeyDown: true, At(0), injected: true);

        Assert.Equal(before, hook.Version);
    }

    [Fact]
    public void ReleasingAKeyIsNotASecondPieceOfActivity()
    {
        using var hook = new LowLevelKeyboardHook();

        hook.RecordHookEvent(LetterK, isKeyDown: true, At(0));
        var afterDown = hook.Version;
        hook.RecordHookEvent(LetterK, isKeyDown: false, At(40));

        Assert.Equal(afterDown, hook.Version);
    }

    [Theory]
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    [InlineData(0x020B)] // WM_XBUTTONDOWN
    public void AMouseButtonCountsAsActivity(int message)
    {
        // A click is how a caret gets moved without a single key being pressed.
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordMouseEvent(message, injected: false);

        Assert.NotEqual(before, hook.Version);
    }

    [Theory]
    [InlineData(0x0200)] // WM_MOUSEMOVE
    [InlineData(0x0202)] // WM_LBUTTONUP
    [InlineData(0x020A)] // WM_MOUSEWHEEL
    public void MovingOrScrollingTheMouseIsNotActivity(int message)
    {
        // Neither moves a caret, and a pointer that is merely resting on the desk twitches often
        // enough that counting movement would make the memory useless.
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordMouseEvent(message, injected: false);

        Assert.Equal(before, hook.Version);
    }

    [Fact]
    public void ASyntheticClickIsNotActivity()
    {
        using var hook = new LowLevelKeyboardHook();
        var before = hook.Version;

        hook.RecordMouseEvent(0x0201, injected: true);

        Assert.Equal(before, hook.Version);
    }

    [Fact]
    public void RecordingActivityAllocatesNothing()
    {
        // The same rule as the keyboard callback, for the same reason: a hook that allocates can
        // stall on a collection, and Windows removes a hook that stalls.
        using var hook = new LowLevelKeyboardHook();

        for (var i = 0; i < 2_000; i++)
        {
            hook.RecordMouseEvent(0x0201, injected: false);
            hook.RecordHookEvent(LetterK, i % 2 == 0, At(i));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            hook.RecordMouseEvent(0x0201, injected: false);
            hook.RecordHookEvent(LetterK, i % 2 == 0, At(i));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void EscapeIsSwallowedOnlyWhileAPipelineIsInFlight()
    {
        // Esc is the single key Jane ever takes from the app underneath, and only while there is
        // something to cancel. Idle Esc must reach the editor, the dialog, the game.
        using var hook = new LowLevelKeyboardHook();

        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, At(0)));

        // An idle key-down passes through, so its key-up must too: an application that saw the
        // press has to see the release.
        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, At(5)));

        hook.NotifyPipelineActive(true);
        Assert.True(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, At(10)));

        // ...and a key-down Jane hid takes its key-up with it. This used to pass the up through,
        // which handed the application underneath a release for a press it never received.
        Assert.True(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, At(20)));

        hook.NotifyPipelineActive(false);
        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, At(30)));
    }

    [Fact]
    public void HookReportsHoldDuration()
    {
        var (machine, events) = NewMachine();

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(1_200)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));
        Assert.Equal(TimeSpan.Zero, events[0].Duration);
        Assert.Equal(1_200, events[1].Duration.TotalMilliseconds, tolerance: 2);
    }

    [Fact]
    public void SubMinimumHoldCancelsSilently()
    {
        // Right Ctrl is a common game bind and sits under the little finger. A brush of the key
        // must cost nothing at all -- no overlay, no model load, no text.
        var (machine, events) = NewMachine(options: new HotkeyOptions { MinimumHold = TimeSpan.FromMilliseconds(300) });

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(200)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.TooShort], events.Select(e => e.Kind));
        Assert.Equal(200, events[1].Duration.TotalMilliseconds, tolerance: 2);
        Assert.False(machine.IsActive);
    }

    [Fact]
    public void HoldModeReleasesOnKeyUpSoTheBoundKeyIsPhysicallyUpByThen()
    {
        // Phase 3's ModifierGate depends on knowing this: in hold mode the bound modifier is
        // already up when Released fires, so it is never itself the modifier that blocks
        // injection. Other modifiers still can be, which is why the gate still exists.
        var (machine, events) = NewMachine();

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        Assert.True(machine.IsHeld);

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(900)), events.Add);

        Assert.False(machine.IsHeld);
        Assert.Equal(HotkeyEventKind.Released, events[^1].Kind);
    }

    [Fact]
    public void ToggleModeReleasesOnTheSecondKeyDownSoTheBoundKeyIsStillPhysicallyDown()
    {
        // The mirror image, and the case that makes Phase 3's gate mandatory rather than
        // belt-and-braces: injection can begin while Right Ctrl is physically held.
        var (machine, events) = NewMachine(HotkeyMode.Toggle);

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(80)), events.Add);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(4_000)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));
        Assert.Equal(4_000, events[1].Duration.TotalMilliseconds, tolerance: 5);
        Assert.True(machine.IsHeld);
        Assert.False(machine.IsActive);
    }

    [Fact]
    public void ToggleModeAutoStopsAtMaxDuration()
    {
        // A forgotten toggle would otherwise dictate until the machine ran out of memory.
        var (machine, events) = NewMachine(HotkeyMode.Toggle, new HotkeyOptions
        {
            MaxDuration = TimeSpan.FromSeconds(2),
        });

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        machine.Tick(At(1_500), events.Add);
        Assert.Single(events);

        machine.Tick(At(2_100), events.Add);

        // Released, not Cancelled: the plan says this case proceeds through the pipeline.
        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));
        Assert.False(machine.IsActive);
    }

    [Fact]
    public void MaxDurationAlsoRescuesAHoldWhoseKeyUpNeverArrived()
    {
        // A key-up lost to a UAC prompt or a session switch would otherwise leave Jane recording
        // forever with no way back.
        var (machine, events) = NewMachine(options: new HotkeyOptions { MaxDuration = TimeSpan.FromSeconds(2) });

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        machine.Tick(At(3_000), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));

        // The eventual key-up must not produce a second Released for the same dictation.
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(4_000)), events.Add);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void AutoRepeatKeyDownsDoNotRestartTheHold()
    {
        // Windows repeats WM_KEYDOWN while a key is held. Treating each as a fresh press would
        // reset the hold clock and turn a two-second dictation into a sub-minimum cancel.
        var (machine, events) = NewMachine();

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);
        for (var repeat = 1; repeat <= 20; repeat++)
        {
            machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(repeat * 30)), events.Add);
        }

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(1_000)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));
        Assert.Equal(1_000, events[1].Duration.TotalMilliseconds, tolerance: 2);
    }

    [Fact]
    public void EscapeCancelsADictationInFlightAndIsIgnoredOtherwise()
    {
        var (machine, events) = NewMachine();

        machine.Handle(new RawKeyEvent(HotkeyBinding.VkEscape, IsDown: true, At(0)), events.Add);
        Assert.Empty(events);

        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(100)), events.Add);
        machine.Handle(new RawKeyEvent(HotkeyBinding.VkEscape, IsDown: true, At(900)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Cancelled], events.Select(e => e.Kind));
        Assert.Equal(800, events[1].Duration.TotalMilliseconds, tolerance: 2);
        Assert.False(machine.IsActive);

        // The hotkey is still physically down; letting go must not then run the pipeline.
        Assert.True(machine.IsHeld);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(1_500)), events.Add);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ABindingWithRequiredModifiersOnlyFiresWhenTheyAreDown()
    {
        // The default binding is a bare modifier, but the picker allows combinations, and the
        // hook is the only place that knows which physical keys are down.
        var (machine, events) = NewMachine(binding: new HotkeyBinding(LetterK, [LeftShift]));

        machine.Handle(new RawKeyEvent(LetterK, IsDown: true, At(0)), events.Add);
        machine.Handle(new RawKeyEvent(LetterK, IsDown: false, At(500)), events.Add);
        Assert.Empty(events);

        machine.Handle(new RawKeyEvent(LeftShift, IsDown: true, At(600)), events.Add);
        machine.Handle(new RawKeyEvent(LetterK, IsDown: true, At(700)), events.Add);
        machine.Handle(new RawKeyEvent(LetterK, IsDown: false, At(1_400)), events.Add);

        Assert.Equal([HotkeyEventKind.Pressed, HotkeyEventKind.Released], events.Select(e => e.Kind));
    }

    [Fact]
    public void RebindingMidHoldDropsTheHoldRatherThanFiringOnTheNewKey()
    {
        var (machine, events) = NewMachine();
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: true, At(0)), events.Add);

        machine.Rebind(new HotkeyBinding(LetterK, []), HotkeyMode.Toggle);

        Assert.False(machine.IsActive);
        Assert.False(machine.IsHeld);
        machine.Handle(new RawKeyEvent(RightCtrl, IsDown: false, At(900)), events.Add);
        Assert.Single(events);

        machine.Handle(new RawKeyEvent(LetterK, IsDown: true, At(1_000)), events.Add);
        Assert.Equal(HotkeyEventKind.Pressed, events[^1].Kind);
        Assert.Equal(HotkeyMode.Toggle, machine.Mode);
    }

    [Fact]
    public void WatchdogReinstallsARemovedHook()
    {
        // Windows removes a hook whose callback overran LowLevelHooksTimeout and tells nobody.
        // Without this the hotkey simply stops working, with no error anywhere to explain it.
        var hook = new FakeHook { IsInstalled = false };
        using var watchdog = new HookWatchdog(hook, TimeSpan.FromMilliseconds(50));

        Assert.True(watchdog.Check());

        Assert.Equal(1, hook.ReinstallCount);
        Assert.Equal(1, watchdog.ReinstallCount);
        Assert.True(hook.IsInstalled);
    }

    [Fact]
    public void WatchdogLeavesAHealthyHookAlone()
    {
        var hook = new FakeHook();
        using var watchdog = new HookWatchdog(hook, TimeSpan.FromMilliseconds(50));

        Assert.False(watchdog.Check());
        Assert.False(watchdog.Check());

        Assert.Equal(0, hook.ReinstallCount);
    }

    [Fact]
    public void WatchdogKeepsRetryingWhileReinstallationFails()
    {
        // A failed re-install is not a reason to give up: the usual cause is a transient desktop
        // switch, and the next sweep succeeds.
        var hook = new FakeHook { IsInstalled = false, ReinstallSucceeds = false };
        using var watchdog = new HookWatchdog(hook, TimeSpan.FromMilliseconds(50));

        watchdog.Check();
        watchdog.Check();
        hook.ReinstallSucceeds = true;
        watchdog.Check();

        Assert.Equal(3, hook.ReinstallCount);
        Assert.True(hook.IsInstalled);
        Assert.False(watchdog.Check());
    }

    [Fact]
    public void WatchdogRunsOnItsOwnTimerOnceStarted()
    {
        var hook = new FakeHook { IsInstalled = false };
        using var watchdog = new HookWatchdog(hook, TimeSpan.FromMilliseconds(10));

        watchdog.Start();

        var deadline = Stopwatch.StartNew();
        while (hook.ReinstallCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Assert.True(hook.ReinstallCount >= 1);
    }

    [Fact]
    public void QueuePreservesOrderAcrossAWrap()
    {
        var queue = new RawKeyEventQueue(8);

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.True(queue.TryEnqueue(new RawKeyEvent((round * 4) + i, IsDown: true, At(i))));
            }

            for (var i = 0; i < 4; i++)
            {
                Assert.True(queue.TryDequeue(out var dequeued));
                Assert.Equal((round * 4) + i, dequeued.VirtualKey);
            }
        }

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void QueueDropsRatherThanBlockingWhenFull()
    {
        // The producer is the hook proc. It cannot wait, cannot grow the ring, and cannot take a
        // lock -- so an overrun is counted and dropped, and the counter is what makes the
        // condition visible rather than silent.
        var queue = new RawKeyEventQueue(8);

        for (var i = 0; i < queue.Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(new RawKeyEvent(i, IsDown: true, At(i))));
        }

        Assert.False(queue.TryEnqueue(new RawKeyEvent(999, IsDown: true, At(0))));
        Assert.Equal(1, queue.DroppedCount);
    }

    private sealed class FakeHook : IKeyboardHookHandle
    {
        public bool IsInstalled { get; set; } = true;

        public bool ReinstallSucceeds { get; set; } = true;

        public int ReinstallCount { get; private set; }

        public bool Reinstall()
        {
            ReinstallCount++;
            IsInstalled = ReinstallSucceeds;
            return ReinstallSucceeds;
        }
    }
}
