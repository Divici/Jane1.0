using Jane.Core.Abstractions;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// When it is safe to give the user their clipboard back.
/// </summary>
/// <remarks>
/// <para>
/// Jane borrows the clipboard, synthesises Ctrl+V, and must then put back what was there. The
/// question is when. The target reads the clipboard while processing the key message, which
/// happens some unspecified time after <c>SendInput</c> returns -- a property of that
/// application's message loop, not of Jane.
/// </para>
/// <para>
/// The old answer was a flat 60 ms, which is a race with a name. Windows 11's Notepad is a WinUI
/// application and under load takes considerably longer, and when it does the user's own clipboard
/// is what lands in the document -- one of the two mechanisms that fit the field report of a long
/// dictation arriving as something other than the text.
/// </para>
/// <para>
/// The new answer waits for evidence: the clipboard's sequence number moves when anyone opens it,
/// and the target opening it to read the paste is exactly the event worth waiting for. A ceiling
/// keeps a target that never reads from holding the clipboard hostage, and the result says which
/// of the two ended the wait -- because "confirmed" and "gave up" are very different stories when
/// a paste comes out wrong.
/// </para>
/// </remarks>
[Trait("Category", "Injection")]
public sealed class ClipboardSettleTests
{
    private static readonly ModifierGateOptions FastGate =
        new(Timeout: TimeSpan.FromMilliseconds(200), PollInterval: TimeSpan.FromMilliseconds(1));

    private static readonly ClipboardInjectorOptions Settling = new(
        PasteSettleDelay: TimeSpan.FromMilliseconds(1),
        PasteSettleCeiling: TimeSpan.FromMilliseconds(400),
        PasteSettlePoll: TimeSpan.FromMilliseconds(2));

    private static readonly TargetWindow Notepad =
        new(Handle: 0x00BEEF, ProcessId: 4242, ProcessName: "notepad", WindowClass: "Notepad", WindowTitle: "Untitled");

    [Fact]
    public async Task ATargetThatReadsPromptlyEndsTheWaitEarly()
    {
        // The target reads the clipboard while processing the key message, so the read is modelled
        // as happening during the send rather than after it.
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput
        {
            OnSend = batch =>
            {
                clipboard.SimulateRead();
                return new SendInputOutcome((uint)batch.Length, 0);
            },
        };

        var result = await NewInjector(clipboard, send)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("confirmed", result.Diagnostics.PasteSettleReason);
        Assert.True(result.Diagnostics.PasteSettle < TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task ATargetThatNeverReadsIsGivenUpOnAtTheCeiling()
    {
        // The clipboard still comes back. A user whose copied text vanished because one
        // application ignored a paste would be worse off than one whose dictation failed.
        var clipboard = FakeClipboard.WithUsersData();

        var result = await NewInjector(clipboard, new FakeSendInput())
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("ceiling", result.Diagnostics.PasteSettleReason);
        Assert.True(result.Diagnostics.PasteSettle >= TimeSpan.FromMilliseconds(400));
        Assert.True(result.Diagnostics.ClipboardRestored);
    }

    [Fact]
    public async Task ASlowTargetIsStillWaitedForRatherThanRacedWith()
    {
        // The bug, stated directly: 60 ms was not enough for this target and 400 ms is.
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var injector = NewInjector(clipboard, send);

        var token = TestContext.Current.CancellationToken;
        var reading = Task.Run(
            async () =>
            {
                await Task.Delay(120, token);
                clipboard.SimulateRead();
            },
            token);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);
        await reading;

        Assert.Equal("confirmed", result.Diagnostics.PasteSettleReason);
        Assert.True(result.Diagnostics.PasteSettle >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task TheUsersClipboardIsAlwaysPutBackWhicheverWayTheWaitEnded()
    {
        var clipboard = FakeClipboard.WithUsersData();
        var before = clipboard.Snapshot().Select(p => p.Format).ToArray();

        var result = await NewInjector(clipboard, new FakeSendInput())
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Diagnostics.ClipboardRestored);
        Assert.Equal(before, clipboard.Snapshot().Select(p => p.Format).ToArray());
    }

    [Fact]
    public void TheShippedDefaultsWaitLongEnoughForASlowWinUiTarget()
    {
        // Pinned, because the number that was wrong before was a default nobody revisited.
        Assert.Equal(TimeSpan.FromMilliseconds(750), ClipboardInjectorOptions.Default.PasteSettleCeiling);
        Assert.True(ClipboardInjectorOptions.Default.PasteSettleDelay > TimeSpan.Zero);
        Assert.True(ClipboardInjectorOptions.Default.PasteSettlePoll > TimeSpan.Zero);
    }

    private static ClipboardInjector NewInjector(FakeClipboard clipboard, FakeSendInput send) =>
        new(new FakeFocusTracker(Notepad), new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(new FakeAsyncKeyState(), FastGate), Settling);
}
