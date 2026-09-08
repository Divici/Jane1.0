using System.Diagnostics;

namespace Jane.Windows.Injection;

/// <param name="Timeout">
/// How long to wait for the user's fingers to leave the modifiers. 500 ms is long enough to
/// cover the natural lag between "I stopped talking" and "I let go", and short enough that an
/// abort still feels like a response rather than a hang.
/// </param>
/// <param name="PollInterval">
/// <c>GetAsyncKeyState</c> has no wait form, so release is detected by polling. 10 ms costs a
/// handful of P/Invokes per dictation and bounds the added latency at one interval.
/// </param>
public sealed record ModifierGateOptions(TimeSpan Timeout, TimeSpan PollInterval)
{
    public static ModifierGateOptions Default { get; } = new(
        Timeout: TimeSpan.FromMilliseconds(500),
        PollInterval: TimeSpan.FromMilliseconds(10));
}

/// <param name="Cleared">True when every modifier was up. Only then may text be injected.</param>
/// <param name="InitiallyHeld">What was down on the first poll -- diagnostic, not a failure.</param>
/// <param name="StillHeld">What was down when the wait expired. Empty when <paramref name="Cleared"/>.</param>
/// <param name="Polls">Poll rounds performed, including the first. Always at least one.</param>
public sealed record ModifierGateResult(
    bool Cleared,
    IReadOnlyList<int> InitiallyHeld,
    IReadOnlyList<int> StillHeld,
    TimeSpan Waited,
    int Polls)
{
    /// <summary>Names the offending keys for the overlay toast that follows an abort.</summary>
    public string Describe() => StillHeld.Count == 0
        ? "no modifiers held"
        : string.Join(" + ", StillHeld.Select(VirtualKeys.NameOf));
}

/// <summary>
/// Refuses to let text out while a modifier key is physically down.
/// </summary>
/// <remarks>
/// <para>
/// This is the guard on the most catastrophic failure mode in Jane. The default hotkey is Right
/// Ctrl <em>held alone</em>, so at the moment the pipeline finishes and injection begins, the
/// user's finger is frequently still on Ctrl. Text injected in that state does not land as text:
/// every character becomes a control chord. In a shell that is not misplaced input, it is
/// execution -- Ctrl+C, Ctrl+D, Ctrl+Z on a sentence's worth of characters.
/// </para>
/// <para>
/// The gate polls rather than hooks deliberately: a hook would only see keys routed through the
/// input queue, whereas <c>GetAsyncKeyState</c> reports the physical state regardless of which
/// window has focus, which is the question that actually matters here.
/// </para>
/// </remarks>
public sealed class ModifierGate(IAsyncKeyState keyState, ModifierGateOptions? options = null)
{
    private readonly ModifierGateOptions _options = options ?? ModifierGateOptions.Default;

    /// <summary>
    /// The physical key state this gate reads.
    /// </summary>
    /// <remarks>
    /// Exposed so an injector can ask the same source again on the way out. Checking with a second
    /// key-state object would be checking a second thing: these are process-wide Win32 reads in
    /// production, but a test that gates on one fake and verifies against another is asserting
    /// nothing about the code under test.
    /// </remarks>
    public IAsyncKeyState KeyState => keyState;

    /// <summary>Every modifier Jane refuses to type underneath, sided variants and aggregates.</summary>
    public static IReadOnlyList<int> ModifierVirtualKeys { get; } =
    [
        VirtualKeys.LeftControl, VirtualKeys.RightControl, VirtualKeys.Control,
        VirtualKeys.LeftShift, VirtualKeys.RightShift, VirtualKeys.Shift,
        VirtualKeys.LeftAlt, VirtualKeys.RightAlt, VirtualKeys.Alt,
        VirtualKeys.LeftWindows, VirtualKeys.RightWindows,
    ];

    /// <summary>
    /// Polls until every modifier is up, or the timeout expires with one still held.
    /// </summary>
    /// <remarks>
    /// Every poll reads every modifier rather than stopping at the first hit, so the abort
    /// message can name all of them -- "Right Ctrl + Left Shift still held" is actionable in a
    /// way that "a modifier is held" is not.
    /// </remarks>
    public async Task<ModifierGateResult> WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var polls = 0;
        IReadOnlyList<int>? initiallyHeld = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var held = Poll();
            polls++;
            initiallyHeld ??= held;

            if (held.Count == 0)
            {
                return new ModifierGateResult(
                    Cleared: true,
                    InitiallyHeld: initiallyHeld,
                    StillHeld: [],
                    Waited: Stopwatch.GetElapsedTime(startedAt),
                    Polls: polls);
            }

            var waited = Stopwatch.GetElapsedTime(startedAt);
            if (waited >= _options.Timeout)
            {
                return new ModifierGateResult(
                    Cleared: false,
                    InitiallyHeld: initiallyHeld,
                    StillHeld: held,
                    Waited: waited,
                    Polls: polls);
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<int> Poll()
    {
        List<int> held = [];
        foreach (var virtualKey in ModifierVirtualKeys)
        {
            if (keyState.IsPhysicallyDown(virtualKey))
            {
                held.Add(virtualKey);
            }
        }

        return held;
    }
}
