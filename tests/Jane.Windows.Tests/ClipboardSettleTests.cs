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
    public async Task TheWaitRunsItsFullLengthAndSaysThatIsWhatItDid()
    {
        // A read does not move the clipboard sequence number, so there is no confirmation to wait
        // for and the code no longer pretends there is. Every clipboard injection in the field log
        // ended at the ceiling while claiming to be watching for a signal that cannot occur.
        var clipboard = FakeClipboard.WithUsersData();

        var result = await NewInjector(clipboard, new FakeSendInput())
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("elapsed", result.Diagnostics.PasteSettleReason);
        Assert.True(result.Diagnostics.PasteSettle >= TimeSpan.FromMilliseconds(350));
        Assert.True(result.Diagnostics.ClipboardRestored);
    }

    [Fact]
    public async Task AnotherApplicationTakingTheClipboardStopsJaneOverwritingIt()
    {
        // What the sequence number is actually good for. If something else wrote while Jane was
        // borrowing, restoring the old contents would throw away what they had just copied.
        var clipboard = FakeClipboard.WithUsersData();
        var token = TestContext.Current.CancellationToken;

        var somebodyElseCopies = Task.Run(
            async () =>
            {
                await Task.Delay(80, token);
                clipboard.SimulateRead();
            },
            token);

        var result = await NewInjector(clipboard, new FakeSendInput())
            .InjectAsync("hello", Notepad, token);
        await somebodyElseCopies;

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("taken-over", result.Diagnostics.PasteSettleReason);
        Assert.False(result.Diagnostics.ClipboardRestored);
    }

    [Fact]
    public async Task TheUsersClipboardIsPutBackWhenNothingElseClaimedIt()
    {
        var clipboard = FakeClipboard.WithUsersData();
        var before = clipboard.Snapshot().Select(p => p.Format).ToArray();

        var result = await NewInjector(clipboard, new FakeSendInput())
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Diagnostics.ClipboardRestored);
        Assert.Equal(before, clipboard.Snapshot().Select(p => p.Format).ToArray());
    }

    [Fact]
    public async Task AnInjectionThatThrowsStillGivesTheClipboardBack()
    {
        // The abort paths carry a default settle value, so "was anything else holding it" has to
        // default to no. A flag that had to be set to get the user's clipboard back would lose it
        // on exactly the paths nobody rehearses.
        var clipboard = FakeClipboard.WithUsersData();
        var before = clipboard.Snapshot().Select(p => p.Format).ToArray();
        var send = new FakeSendInput
        {
            OnSend = _ => throw new InvalidOperationException("SendInput blew up mid-chord."),
        };

        var result = await NewInjector(clipboard, send)
            .InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
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
