using Jane.Core.Abstractions;
using Jane.Windows.Automation;

namespace Jane.Windows.Injection;

/// <param name="Detail">Names the specific cause, for the overlay toast and the history row.</param>
internal readonly record struct PreflightAbort(InjectionFailure Failure, string Detail);

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
    public async Task<PreflightAbort?> CheckAsync(TargetWindow target, CancellationToken cancellationToken)
    {
        if (target.IsNone)
        {
            return new PreflightAbort(
                InjectionFailure.TargetGone,
                "No target window was captured at key-down, so there is nowhere safe to type.");
        }

        var gate = await modifierGate.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (!gate.Cleared)
        {
            return new PreflightAbort(
                InjectionFailure.ModifierHeld,
                $"Still held after {gate.Waited.TotalMilliseconds:F0} ms: {gate.Describe()}. Typing now would send control chords, not text.");
        }

        if (!liveness.IsAlive(target.Handle))
        {
            return new PreflightAbort(
                InjectionFailure.TargetGone,
                $"The {DescribeApp(target)} window closed while Jane was working.");
        }

        var current = focusTracker.GetForegroundWindow();
        if (!current.MatchesIdentity(target))
        {
            return new PreflightAbort(
                InjectionFailure.TargetChanged,
                $"Focus moved from {DescribeApp(target)} to {DescribeApp(current)} while Jane was working.");
        }

        return null;
    }

    private static string DescribeApp(TargetWindow window) =>
        window.IsNone ? "another window"
        : string.IsNullOrEmpty(window.ProcessName) ? $"window 0x{window.Handle:X}"
        : window.ProcessName;
}
