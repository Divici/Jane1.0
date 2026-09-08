using Jane.App.Composition;
using Jane.Core.Platform;
using Jane.Core.Settings;

namespace Jane.App.Tests;

/// <summary>
/// Whether the first-run window opens, and where that answer comes from.
/// </summary>
/// <remarks>
/// <para>
/// The field report: onboarding ran on every single boot. Settings moved into SQLite at Phase 10
/// and <see cref="FirstRunViewModel.FinishAsync"/> writes the completion flag there, but
/// <c>JaneHost.Settings</c> still returned the Phase 1 JSON file, which nothing had written since
/// the migration. Every launch read a file that said <c>onboardingComplete: false</c> and opened
/// the wizard, and finishing the wizard wrote the flag somewhere the next launch never looked.
/// </para>
/// <para>
/// Two sources of truth for one fact is the whole bug, so the fix is not to make the two agree --
/// it is to delete one of them. <see cref="JaneHost"/> no longer holds a
/// <see cref="SettingsStore"/> at all; the JSON file is opened inside <c>Create</c>, read once for
/// a <c>bench</c> result, and closed. There is nothing left to read the wrong thing from.
/// </para>
/// <para>
/// The gate itself lives here rather than inline in <c>App.StartHostAsync</c>, because a decision
/// that costs a user a wizard on every reboot deserves a test, and a WPF <c>Application</c>
/// subclass cannot be constructed in one.
/// </para>
/// </remarks>
public sealed class StartupPolicyTests
{
    [Fact]
    public void AFirstEverLaunchOpensTheWizard()
    {
        var decision = StartupPolicy.Decide(new JaneSettings(), skipRequested: false);

        Assert.True(decision.ShouldRunOnboarding);
        Assert.Equal(StartupOnboardingReason.NotYetCompleted, decision.Reason);
    }

    [Fact]
    public void ACompletedOnboardingIsNeverRunAgain()
    {
        var decision = StartupPolicy.Decide(new JaneSettings { OnboardingComplete = true }, skipRequested: false);

        Assert.False(decision.ShouldRunOnboarding);
        Assert.Equal(StartupOnboardingReason.AlreadyCompleted, decision.Reason);
    }

    [Fact]
    public void TheSkipVariableSuppressesTheWizardEvenOnAFirstRun()
    {
        // A test-launched Jane must never open onboarding: it would steal the foreground and stop
        // being idle, which is the one thing the idle-footprint measurement is measuring.
        var decision = StartupPolicy.Decide(new JaneSettings(), skipRequested: true);

        Assert.False(decision.ShouldRunOnboarding);
        Assert.Equal(StartupOnboardingReason.Suppressed, decision.Reason);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("true", false)]
    public void OnlyTheExactValueOneSuppressesIt(string? value, bool expected)
    {
        // Exactly "1", as documented on the constant. A variable left set to "0" by a previous
        // run must not silently disable first run on a real machine.
        Assert.Equal(expected, StartupPolicy.SkipRequested(value));
    }

    [Fact]
    public async Task TheCompletionFlagComesFromTheDatabaseEvenWhenTheJsonFileDisagrees()
    {
        // The regression itself. The file is stale by construction -- it has not been written
        // since the Phase 10 migration -- so a disagreement is the normal state, not an edge case.
        using var temp = new TempJane();

        await temp.Settings.WriteAsync(
            temp.Settings.Current with { OnboardingComplete = true },
            TestContext.Current.CancellationToken);

        using var benchFile = new SettingsStore(new JanePaths(temp.Root));
        await benchFile.WriteAsync(
            new JaneSettings { OnboardingComplete = false, BenchmarkedAt = DateTimeOffset.Now },
            TestContext.Current.CancellationToken);

        var effective = JaneHost.ReadStartupSettings(benchFile, temp.Settings);

        Assert.True(effective.OnboardingComplete);
        Assert.False(StartupPolicy.Decide(effective, skipRequested: false).ShouldRunOnboarding);
    }

    [Fact]
    public void TheHostNoLongerHoldsAJsonSettingsStoreToReadTheWrongAnswerFrom()
    {
        // Structural, and deliberately so: the bug was a field that should not have existed, and
        // the only durable fix is that it does not. A future edit that reintroduces one fails here
        // rather than on somebody's machine six weeks later.
        var fields = typeof(JaneHost).GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(fields, field => field.FieldType == typeof(SettingsStore));
    }
}
