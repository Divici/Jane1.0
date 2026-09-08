using Jane.Core.Abstractions;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// Jane must never leave a key down that it pressed.
/// </summary>
/// <remarks>
/// <para>
/// The field report: a long dictation into Notepad produced a run of dots, and then every
/// subsequent keystroke produced more dots. The second half is the interesting one -- it outlives
/// the dictation entirely, which means something Jane did changed the state of the keyboard rather
/// than the contents of a window.
/// </para>
/// <para>
/// The clipboard strategy synthesises Ctrl+V as four records in one <c>SendInput</c> call: Ctrl
/// down, V down, V up, Ctrl up. <c>SendInput</c> is documented to stop at the first record another
/// thread's input blocks, and it reports how many it took. The old code compared that count and
/// threw -- <em>after</em> a Ctrl-down had already gone out and before any Ctrl-up ever would.
/// Windows then believes Ctrl is held by nobody, forever, and every later keystroke arrives as a
/// control chord.
/// </para>
/// <para>
/// So: the ups are guaranteed, and every injection ends by checking that nothing Jane pressed is
/// still physically down. The check is cheap -- two <c>GetAsyncKeyState</c> calls -- and the thing
/// it prevents is a keyboard the user has to reboot to fix.
/// </para>
/// </remarks>
[Trait("Category", "Injection")]
public sealed class StuckModifierTests
{
    private static readonly ModifierGateOptions FastGate =
        new(Timeout: TimeSpan.FromMilliseconds(200), PollInterval: TimeSpan.FromMilliseconds(1));

    private static readonly ClipboardInjectorOptions NoSettle =
        ClipboardInjectorOptions.Default with { PasteSettleDelay = TimeSpan.Zero };

    private static readonly TargetWindow Notepad =
        new(Handle: 0x00BEEF, ProcessId: 4242, ProcessName: "notepad", WindowClass: "Notepad", WindowTitle: "Untitled");

    [Fact]
    public async Task AChordThatWasOnlyPartlyAcceptedStillGetsItsKeyUps()
    {
        // The exact shape of the bug: Windows takes the Ctrl-down and refuses the rest.
        var send = new FakeSendInput
        {
            OnSend = batch => batch.Length == 4
                ? new SendInputOutcome(Accepted: 1, LastError: 0)
                : new SendInputOutcome((uint)batch.Length, 0),
        };

        var result = await NewClipboard(FakeClipboard.WithUsersData(), send)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);

        // Whatever else happened, Ctrl must have been released.
        Assert.Contains(
            send.AllRecords,
            r => r.Keyboard.VirtualKey == VirtualKeys.Control && (r.Keyboard.Flags & KeyEventFlags.KeyUp) != 0);
    }

    [Fact]
    public async Task TheKeyUpsGoOutEvenWhenTheChordThrows()
    {
        // Send itself failing, rather than reporting a short count, is the same hazard by another
        // route: a Ctrl-down may already have reached the system before the throw.
        var attempts = 0;
        var send = new FakeSendInput
        {
            OnSend = _ => ++attempts == 1
                ? throw new InvalidOperationException("SendInput blew up mid-chord.")
                : new SendInputOutcome(2, 0),
        };

        var result = await NewClipboard(FakeClipboard.WithUsersData(), send)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(attempts >= 2, "The release must be attempted even after the chord throws.");
    }

    [Fact]
    public async Task AModifierLeftDownIsReleasedAndReported()
    {
        // Belt and braces on top of the guaranteed ups: whatever the cause, an injection that ends
        // with Ctrl physically down releases it rather than handing the user a broken keyboard.
        var keys = new FakeAsyncKeyState();
        keys.HoldFromPoll(VirtualKeys.Control, fromPoll: 2);
        var send = new FakeSendInput();

        var result = await NewClipboard(FakeClipboard.WithUsersData(), send, keys)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("Ctrl", result.Diagnostics.ModifiersStuckAfter);

        var releases = send.AllRecords
            .Where(r => r.Keyboard.VirtualKey == VirtualKeys.Control && (r.Keyboard.Flags & KeyEventFlags.KeyUp) != 0)
            .ToList();
        Assert.NotEmpty(releases);
    }

    [Fact]
    public async Task AHealthyInjectionReportsNothingStuckAndSendsNoExtraKeys()
    {
        var send = new FakeSendInput();

        var result = await NewClipboard(FakeClipboard.WithUsersData(), send)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Null(result.Diagnostics.ModifiersStuckAfter);

        // Exactly the paste chord: nothing added defensively when nothing was wrong.
        Assert.Equal(4, send.AllRecords.Count);
    }

    [Fact]
    public async Task TheUnicodePathAlsoChecksThatNothingIsLeftHeld()
    {
        // It presses no modifiers of its own, but a user still holding Ctrl from something else,
        // past the gate's timeout, would turn typed text into chords. The check is the same one.
        var keys = new FakeAsyncKeyState();
        keys.HoldFromPoll(VirtualKeys.Shift, fromPoll: 2);
        var send = new FakeSendInput();
        var injector = new SendInputInjector(
            new FakeFocusTracker(Notepad), new FakeWindowLiveness(), send, new ModifierGate(keys, FastGate));

        var result = await injector.InjectAsync("hi", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("Shift", result.Diagnostics.ModifiersStuckAfter);
    }

    private static ClipboardInjector NewClipboard(
        FakeClipboard clipboard, FakeSendInput send, FakeAsyncKeyState? keys = null) =>
        new(new FakeFocusTracker(Notepad), new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(keys ?? new FakeAsyncKeyState(), FastGate), NoSettle);
}
