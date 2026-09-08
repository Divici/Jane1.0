using Jane.Core.Abstractions;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// What each strategy measured on its way through, carried back on the result.
/// </summary>
/// <remarks>
/// <para>
/// A run of dots in the target window, followed by dots on every subsequent keystroke, has two
/// causes that look identical on screen: a modifier Jane left held, and a clipboard restored
/// before the target finished reading it. Nothing in the old <see cref="InjectionResult"/> could
/// tell them apart -- it carried a boolean, a strategy and a character count.
/// </para>
/// <para>
/// These numbers are diagnostics, never control flow. Nothing branches on them, which is why they
/// can be added to every path without changing what any of them do.
/// </para>
/// </remarks>
[Trait("Category", "Injection")]
public sealed class InjectionDiagnosticsTests
{
    private static readonly ModifierGateOptions FastGate =
        new(Timeout: TimeSpan.FromMilliseconds(200), PollInterval: TimeSpan.FromMilliseconds(1));

    private static readonly TargetWindow Notepad =
        new(Handle: 0x00BEEF, ProcessId: 4242, ProcessName: "notepad", WindowClass: "Notepad", WindowTitle: "Untitled");

    [Fact]
    public async Task TheUnicodePathReportsHowManyRecordsWindowsAccepted()
    {
        var send = new FakeSendInput();
        var injector = NewUnicode(send, new FakeAsyncKeyState());

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        // Five characters, two records each.
        Assert.Equal(10, result.Diagnostics.RecordsSent);
        Assert.Equal(10, result.Diagnostics.RecordsAccepted);
    }

    [Fact]
    public async Task APartialRefusalIsVisibleAsAcceptedBelowSent()
    {
        // The failure OpenWhispr#829 describes: Windows quietly stops taking records part-way
        // through and the user sees half a sentence. Without these two numbers the log says only
        // "injection failed", which is the one thing the user already knew.
        var send = new FakeSendInput
        {
            OnSend = batch => new SendInputOutcome(Accepted: (uint)(batch.Length / 2), LastError: 5),
        };
        var injector = NewUnicode(send, new FakeAsyncKeyState());

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Diagnostics.RecordsAccepted < result.Diagnostics.RecordsSent);
        Assert.True(result.Diagnostics.RecordsSent > 0);
    }

    [Fact]
    public async Task WhatTheModifierGateSawSurvivesOntoASuccessfulInjection()
    {
        // Held at the start and released before the timeout is the *normal* case -- the user's
        // finger is still coming off the hotkey. It has to be distinguishable from never-held,
        // because "Jane waited 40 ms for you" explains a latency complaint.
        var keys = new FakeAsyncKeyState();
        keys.HoldForPolls(VirtualKeys.RightControl, polls: 3);
        var injector = NewUnicode(new FakeSendInput(), keys);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("Right Ctrl", result.Diagnostics.ModifiersInitiallyHeld);
        Assert.Null(result.Diagnostics.ModifiersStillHeld);
        Assert.True(result.Diagnostics.ModifierWait > TimeSpan.Zero);
    }

    [Fact]
    public async Task AGateThatNeverClearedNamesWhatWasStillDown()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.RightControl);
        var injector = NewUnicode(new FakeSendInput(), keys);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.ModifierHeld, result.Failure);
        Assert.Equal("Right Ctrl", result.Diagnostics.ModifiersStillHeld);
        Assert.Equal("Right Ctrl", result.Diagnostics.ModifiersInitiallyHeld);
    }

    [Fact]
    public async Task NoModifierAtAllIsReportedAsSuchRatherThanAsMissingData()
    {
        // Distinct from null, which means the gate never ran. "Nobody was holding anything" is a
        // finding.
        var injector = NewUnicode(new FakeSendInput(), new FakeAsyncKeyState());

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal("none", result.Diagnostics.ModifiersInitiallyHeld);
        Assert.Null(result.Diagnostics.ModifiersStillHeld);
    }

    [Fact]
    public async Task TheClipboardPathReportsThatItGaveTheUsersClipboardBack()
    {
        var clipboard = FakeClipboard.WithUsersData();
        var injector = NewClipboard(clipboard, new FakeSendInput());

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Diagnostics.ClipboardRestored);
        Assert.NotNull(result.Diagnostics.PasteSettleReason);
    }

    [Fact]
    public async Task TheUnicodePathLeavesTheClipboardFieldsUnsetBecauseItNeverTouchedIt()
    {
        var injector = NewUnicode(new FakeSendInput(), new FakeAsyncKeyState());

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Null(result.Diagnostics.ClipboardRestored);
        Assert.Null(result.Diagnostics.PasteSettleReason);
    }

    [Fact]
    public async Task TheRouterKeepsTheDiagnosticsOfWhicheverStrategyActuallyRan()
    {
        // The fallback path replaces the result wholesale. Losing the second attempt's numbers
        // would leave the log describing an injection that did not happen.
        var clipboard = new FakeClipboard { FailEnumeration = true };
        var send = new FakeSendInput();
        var router = NewRouter(clipboard, send, Notepad);

        var result = await router.InjectAsync(
            new string('a', 400), Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(InjectionStrategy.Unicode, result.Strategy);
        Assert.Equal(800, result.Diagnostics.RecordsSent);
        Assert.Null(result.Diagnostics.ClipboardRestored);
    }

    private static SendInputInjector NewUnicode(FakeSendInput send, FakeAsyncKeyState keys) =>
        new(new FakeFocusTracker(Notepad), new FakeWindowLiveness(), send, new ModifierGate(keys, FastGate));

    private static ClipboardInjector NewClipboard(FakeClipboard clipboard, FakeSendInput send) =>
        new(new FakeFocusTracker(Notepad), new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(new FakeAsyncKeyState(), FastGate),
            ClipboardInjectorOptions.Default with { PasteSettleDelay = TimeSpan.Zero });

    private static RoutingTextInjector NewRouter(FakeClipboard clipboard, FakeSendInput send, TargetWindow target)
    {
        var focus = new FakeFocusTracker(target);
        var liveness = new FakeWindowLiveness();
        var gate = new ModifierGate(new FakeAsyncKeyState(), FastGate);
        return new RoutingTextInjector(
            new InjectionStrategySelector(),
            new SendInputInjector(focus, liveness, send, gate),
            new ClipboardInjector(
                focus, liveness, clipboard, send, gate,
                ClipboardInjectorOptions.Default with { PasteSettleDelay = TimeSpan.Zero }));
    }
}
