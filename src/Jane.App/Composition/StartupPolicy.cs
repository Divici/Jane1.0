using Jane.Core.Settings;

namespace Jane.App.Composition;

/// <summary>Why the first-run window is or is not about to open. Written to the log either way.</summary>
public enum StartupOnboardingReason
{
    /// <summary>No completed onboarding is recorded. The wizard opens.</summary>
    NotYetCompleted,

    /// <summary>Onboarding finished on some earlier launch. Nothing opens.</summary>
    AlreadyCompleted,

    /// <summary>A test or diagnostic launch asked for it to be skipped.</summary>
    Suppressed,
}

/// <param name="Reason">Names the branch taken, so a wrong answer is legible in the log.</param>
public sealed record StartupDecision(bool ShouldRunOnboarding, StartupOnboardingReason Reason);

/// <summary>
/// The one decision Jane makes before it has a window: does the first-run wizard open?
/// </summary>
/// <remarks>
/// <para>
/// Trivial logic, extracted anyway. It lived inline in <c>App.StartHostAsync</c>, where it could
/// not be tested -- a WPF <see cref="System.Windows.Application"/> subclass cannot be constructed
/// in a unit test -- and it was wrong for months: it read a settings file that had not been
/// written since settings moved into SQLite, so every boot reopened a wizard the user had already
/// finished.
/// </para>
/// <para>
/// A pure function over the settings and one environment variable is small enough to be obviously
/// right, and now says out loud which branch it took.
/// </para>
/// </remarks>
public static class StartupPolicy
{
    public static StartupDecision Decide(JaneSettings settings, bool skipRequested)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (skipRequested)
        {
            return new StartupDecision(false, StartupOnboardingReason.Suppressed);
        }

        return settings.OnboardingComplete
            ? new StartupDecision(false, StartupOnboardingReason.AlreadyCompleted)
            : new StartupDecision(true, StartupOnboardingReason.NotYetCompleted);
    }

    /// <summary>
    /// Whether the skip variable is set to the exact value that means "yes".
    /// </summary>
    /// <remarks>
    /// Exactly <c>"1"</c>. A variable left over from an earlier run as <c>"0"</c> or <c>"false"</c>
    /// must not silently disable first run on a real machine, which anything looser -- a
    /// null-or-empty check, a truthiness test -- would allow.
    /// </remarks>
    public static bool SkipRequested(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);
}
