using System.Diagnostics;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// The gate that stands between a released hotkey and the keyboard.
/// </summary>
/// <remarks>
/// Jane's default hotkey is Right Ctrl held alone, so at the instant the pipeline finishes the
/// user's finger is very often still on a modifier. Unicode text injected into a held-Ctrl state
/// is not misplaced text -- it is a stream of control chords, and in a terminal that is
/// execution. These tests are the guard on the single most catastrophic failure in the plan.
/// </remarks>
[Trait("Category", "Injection")]
public sealed class ModifierGateTests
{
    private static readonly ModifierGateOptions Fast =
        new(Timeout: TimeSpan.FromMilliseconds(500), PollInterval: TimeSpan.FromMilliseconds(1));

    [Fact]
    public void DefaultTimeoutIsTheFiveHundredMillisecondsThePlanSpecifies()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), ModifierGateOptions.Default.Timeout);
        Assert.True(ModifierGateOptions.Default.PollInterval > TimeSpan.Zero);
    }

    [Fact]
    public async Task NothingHeld_ClearsOnTheFirstPollWithoutWaiting()
    {
        var keys = new FakeAsyncKeyState();
        var gate = new ModifierGate(keys, Fast);

        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Cleared);
        Assert.Empty(result.InitiallyHeld);
        Assert.Empty(result.StillHeld);
        Assert.Equal(1, result.Polls);
    }

    [Fact]
    public async Task SyntheticCtrlHeld_BlocksThenProceedsOnceItIsReleased()
    {
        // The plan's named case: "injection while a synthetic Ctrl is held is blocked, then
        // proceeds after release". The fake reports Right Ctrl down for three poll rounds.
        var keys = new FakeAsyncKeyState();
        keys.HoldForPolls(VirtualKeys.RightControl, polls: 3);
        var gate = new ModifierGate(keys, Fast);

        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Cleared);
        Assert.Equal([VirtualKeys.RightControl], result.InitiallyHeld);
        Assert.Empty(result.StillHeld);
        Assert.Equal(4, result.Polls);
    }

    [Fact]
    public async Task ModifierHeldForever_AbortsRatherThanWaitingIndefinitely()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.LeftControl);
        var gate = new ModifierGate(keys, Fast with { Timeout = TimeSpan.FromMilliseconds(120) });

        var stopwatch = Stopwatch.StartNew();
        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.False(result.Cleared);
        Assert.Contains(VirtualKeys.LeftControl, result.StillHeld);
        Assert.True(result.Polls > 1, "the gate must actually re-poll rather than give up at once");

        // Generous upper bound: this asserts boundedness, not scheduler precision.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ZeroTimeout_AbortsAfterExactlyOnePoll()
    {
        // Deterministic companion to the wall-clock test above -- no timing tolerance at all.
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.LeftWindows);
        var gate = new ModifierGate(keys, Fast with { Timeout = TimeSpan.Zero });

        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Cleared);
        Assert.Equal(1, result.Polls);
        Assert.Equal([VirtualKeys.LeftWindows], result.StillHeld);
    }

    [Theory]
    [InlineData(VirtualKeys.LeftControl)]
    [InlineData(VirtualKeys.RightControl)]
    [InlineData(VirtualKeys.Control)]
    [InlineData(VirtualKeys.LeftShift)]
    [InlineData(VirtualKeys.RightShift)]
    [InlineData(VirtualKeys.Shift)]
    [InlineData(VirtualKeys.LeftAlt)]
    [InlineData(VirtualKeys.RightAlt)]
    [InlineData(VirtualKeys.Alt)]
    [InlineData(VirtualKeys.LeftWindows)]
    [InlineData(VirtualKeys.RightWindows)]
    public async Task EveryModifierBlocksInjection_LeftAndRightVariantsAlike(int virtualKey)
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(virtualKey);
        var gate = new ModifierGate(keys, Fast with { Timeout = TimeSpan.Zero });

        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Cleared);
        Assert.Contains(virtualKey, result.StillHeld);
    }

    [Fact]
    public async Task EveryPollReadsEveryModifier_SoDiagnosticsNameAllOfThem()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.RightControl);
        keys.HoldForever(VirtualKeys.LeftShift);
        var gate = new ModifierGate(keys, Fast with { Timeout = TimeSpan.Zero });

        var result = await gate.WaitForReleaseAsync(TestContext.Current.CancellationToken);

        // One poll round, and every modifier read in it -- the loop must not stop at the first
        // key it finds down, or the abort message could name only half the problem.
        Assert.All(ModifierGate.ModifierVirtualKeys, vk => Assert.Equal(1, keys.QueriesFor(vk)));
        Assert.Contains(VirtualKeys.RightControl, result.StillHeld);
        Assert.Contains(VirtualKeys.LeftShift, result.StillHeld);
        Assert.Contains("Right Ctrl", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Left Shift", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_StopsTheWait()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.RightControl);
        var gate = new ModifierGate(keys, ModifierGateOptions.Default);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitForReleaseAsync(cts.Token));
    }

    [Fact]
    public void ModifierListCoversCtrlShiftAltWinInBothVariants()
    {
        int[] required =
        [
            VirtualKeys.LeftControl, VirtualKeys.RightControl,
            VirtualKeys.LeftShift, VirtualKeys.RightShift,
            VirtualKeys.LeftAlt, VirtualKeys.RightAlt,
            VirtualKeys.LeftWindows, VirtualKeys.RightWindows,
        ];

        Assert.All(required, vk => Assert.Contains(vk, ModifierGate.ModifierVirtualKeys));
    }
}

/// <summary>
/// A physical-key-state oracle with no keyboard behind it.
/// </summary>
/// <remarks>
/// Counting queries per virtual key is what makes the "held then released" case deterministic:
/// the gate reads every modifier exactly once per poll round, so a key's query count is the
/// round number, and "release after N polls" needs no clock.
/// </remarks>
internal sealed class FakeAsyncKeyState : IAsyncKeyState
{
    private readonly Dictionary<int, int> _queries = [];
    private readonly Dictionary<int, int> _heldForPolls = [];

    public void HoldForever(int virtualKey) => _heldForPolls[virtualKey] = int.MaxValue;

    public void HoldForPolls(int virtualKey, int polls) => _heldForPolls[virtualKey] = polls;

    /// <summary>
    /// Reports a key as up until the given poll, and down from then on.
    /// </summary>
    /// <remarks>
    /// The shape a stuck modifier actually has: the gate sees a clean keyboard and lets the
    /// injection through, and by the time anything checks again a key is down. Whether the user
    /// pressed it or Jane left it there is exactly what cannot be told apart from here, which is
    /// why the response is to release it either way.
    /// </remarks>
    public void HoldFromPoll(int virtualKey, int fromPoll) => _heldFromPoll[virtualKey] = fromPoll;

    private readonly Dictionary<int, int> _heldFromPoll = [];

    public int QueriesFor(int virtualKey) => _queries.GetValueOrDefault(virtualKey);

    /// <summary>A monotonic read counter, so another fake can record "when" it was called.</summary>
    public int TotalQueries { get; private set; }

    public bool IsPhysicallyDown(int virtualKey)
    {
        var query = _queries.GetValueOrDefault(virtualKey) + 1;
        _queries[virtualKey] = query;
        TotalQueries++;

        if (_heldFromPoll.TryGetValue(virtualKey, out var from) && query >= from)
        {
            return true;
        }

        return _heldForPolls.TryGetValue(virtualKey, out var polls) && query <= polls;
    }
}
