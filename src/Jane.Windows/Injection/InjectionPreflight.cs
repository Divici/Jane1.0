using Jane.Core.Abstractions;
using Jane.Windows.Automation;

namespace Jane.Windows.Injection;

/// <param name="Detail">Names the specific cause, for the overlay toast and the history row.</param>
internal readonly record struct PreflightAbort(InjectionFailure Failure, string Detail);

/// <summary>
/// What the preflight found: whether to abort, and what the modifier gate saw either way.
/// </summary>
/// <remarks>
/// The gate result is returned on the passing path too, which is the whole reason this is a
/// record rather than a nullable abort. "Right Ctrl was down for 40 ms and then cleared" is the
/// normal shape of a hold-to-talk release, and being able to see it is what distinguishes a
/// healthy injection from one that typed underneath a stuck modifier.
/// </remarks>
/// <param name="Note">
/// Something worth recording about an injection that went ahead: focus moved to another window of
/// the same application, say. Null on the ordinary path.
/// </param>
internal readonly record struct PreflightOutcome(PreflightAbort? Abort, ModifierGateResult? Gate, string? Note = null)
{
    /// <summary>Folds the gate result into the diagnostics shape the rest of Jane reads.</summary>
    public InjectionDiagnostics Describe() => Gate is not { } gate
        ? InjectionDiagnostics.Empty
        : new InjectionDiagnostics
        {
            // "none" rather than null: the gate ran and found nothing held, which is a different
            // fact from the gate never having run, and only one of them is worth investigating.
            ModifiersInitiallyHeld = gate.InitiallyHeld.Count == 0
                ? "none"
                : string.Join(" + ", gate.InitiallyHeld.Select(VirtualKeys.NameOf)),
            ModifiersStillHeld = gate.StillHeld.Count == 0 ? null : gate.Describe(),
            ModifierWait = gate.Waited,
        };
}

/// <summary>
/// The two questions both strategies must answer before a single keystroke leaves Jane: are the
/// user's fingers off the modifiers, and is this still the window they were dictating into?
/// </summary>
/// <remarks>
/// Order matters and is not obvious. The modifier wait comes first and the focus check second,
/// because the wait can last up to half a second and the user is perfectly capable of switching
/// windows during it. Checking focus first and typing afterwards would re-open exactly the hole
/// the check exists to close.
/// </remarks>
internal sealed class InjectionPreflight(
    IFocusTracker focusTracker,
    IWindowLiveness liveness,
    ModifierGate modifierGate)
{
    public async Task<PreflightOutcome> CheckAsync(TargetWindow target, CancellationToken cancellationToken)
    {
        if (target.IsNone)
        {
            return new PreflightOutcome(
                new PreflightAbort(
                    InjectionFailure.TargetGone,
                    "No target window was captured at key-down, so there is nowhere safe to type."),
                Gate: null);
        }

        var gate = await modifierGate.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (!gate.Cleared)
        {
            return new PreflightOutcome(
                new PreflightAbort(
                    InjectionFailure.ModifierHeld,
                    $"Still held after {gate.Waited.TotalMilliseconds:F0} ms: {gate.Describe()}. Typing now would send control chords, not text."),
                gate);
        }

        if (!liveness.IsAlive(target.Handle))
        {
            return new PreflightOutcome(
                new PreflightAbort(
                    InjectionFailure.TargetGone,
                    $"The {DescribeApp(target)} window closed while Jane was working."),
                gate);
        }

        var current = focusTracker.GetForegroundWindow();
        if (current.MatchesIdentity(target))
        {
            return new PreflightOutcome(Abort: null, gate);
        }

        // Another window of the same process is still the application the user was dictating
        // into: Chrome's address-bar suggestions, an Electron command palette, a Save dialog. Two
        // of the five injection failures in the field log were "focus moved from chrome to chrome",
        // a finished dictation thrown away over a pop-up. The leak this check exists to stop is a
        // *different* application, and that still aborts.
        if (current.MatchesApplication(target))
        {
            return new PreflightOutcome(
                Abort: null,
                gate,
                Note: $"Focus moved to another {DescribeApp(target)} window (0x{target.Handle:X} to 0x{current.Handle:X}) while Jane was working; the text went to the one that has focus now.");
        }

        return new PreflightOutcome(
            new PreflightAbort(
                InjectionFailure.TargetChanged,
                $"Focus moved from {DescribeApp(target)} to {DescribeApp(current)} while Jane was working."),
            gate);
    }

    private static string DescribeApp(TargetWindow window) =>
        window.IsNone ? "another window"
        : string.IsNullOrEmpty(window.ProcessName) ? $"window 0x{window.Handle:X}"
        : window.ProcessName;
}
