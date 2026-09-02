using Jane.Core.Abstractions;
using Jane.Core.Gpu;
using Jane.Core.Settings;

namespace Jane.Core.Tests;

/// <summary>
/// The routing policy, tested apart from the three Win32/NVML signal sources that feed it.
/// </summary>
/// <remarks>
/// Splitting the policy from the sensors is what makes "a borderless-windowed game routes to
/// LLM-off" assertable at all -- the alternative is launching a real game.
/// <see cref="GpuGovernorTests"/> in Jane.Windows.Tests covers the sensors themselves.
/// </remarks>
public sealed class GovernorTests
{
    private static readonly GpuSettings Defaults = new();

    [Theory]
    [InlineData(GameSignals.NotificationState)]
    [InlineData(GameSignals.FullscreenGeometry)]
    [InlineData(GameSignals.GpuBusy)]
    public void AnySingleGameSignalRoutesToLlmOff(GameSignals signal)
    {
        // Any one is enough. SHQueryUserNotificationState only sees exclusive fullscreen and would
        // miss a borderless-windowed game entirely, so requiring agreement between signals would
        // reintroduce exactly the hole the three-signal design exists to close.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(signal, FreeVramBytes: 8L << 30, GpuUtilisationPercent: 5));

        Assert.Equal(LlmRoute.Skip, decision.Route);
        Assert.True(decision.GameDetected);
        Assert.Equal(signal, decision.Signals & signal);
    }

    [Fact]
    public void NoSignalsAndAFreeGpuRoutesToTheGpu()
    {
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.None, FreeVramBytes: 9L << 30, GpuUtilisationPercent: 2));

        Assert.Equal(LlmRoute.Gpu, decision.Route);
        Assert.False(decision.GameDetected);
    }

    [Fact]
    public void OptingIntoCpuFormattingRoutesToCpuRatherThanSkipping()
    {
        // The plan's opt-in for users who want formatting more than frames.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.UseCpuLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.FullscreenGeometry, 8L << 30, 5));

        Assert.Equal(LlmRoute.Cpu, decision.Route);
        Assert.True(decision.GameDetected);
    }

    [Fact]
    public void LowFreeVramCountsAsBusyEvenWithNoGameDetected()
    {
        // Loading a 2.5 GB model into the last of someone's VRAM causes a stutter in whatever
        // else is using it, game or not.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.None, FreeVramBytes: 512L << 20, GpuUtilisationPercent: 3));

        Assert.Equal(LlmRoute.Skip, decision.Route);
        Assert.Contains(GameSignals.GpuBusy, (GameSignals[])[decision.Signals & GameSignals.GpuBusy]);
    }

    [Fact]
    public void HighGpuUtilisationCountsAsBusy()
    {
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.None, FreeVramBytes: 9L << 30, GpuUtilisationPercent: 85));

        Assert.Equal(LlmRoute.Skip, decision.Route);
    }

    [Fact]
    public void MissingNvmlDoesNotBlockTheGpuRoute()
    {
        // NVML absent is a warning in `doctor`, not a fault. Treating "cannot read VRAM" as
        // "GPU busy" would silently disable formatting on a machine whose driver is simply older.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.None, FreeVramBytes: null, GpuUtilisationPercent: null));

        Assert.Equal(LlmRoute.Gpu, decision.Route);
        Assert.Null(decision.FreeVramBytes);
    }

    [Fact]
    public void DisablingTheLlmEntirelyOverridesEverything()
    {
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm, llmEnabled: false);

        var decision = policy.Decide(new GpuReading(GameSignals.None, 9L << 30, 1));

        Assert.Equal(LlmRoute.Skip, decision.Route);
        Assert.Contains("disabled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GpuDegradedSessionFallsBackWithoutRetryingTheGpu()
    {
        // The open sm_120 MMQ crash is quant-dependent and does not heal. Once a GPU request has
        // taken the server down, retrying it every dictation would mean crashing it every
        // dictation.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);
        policy.MarkGpuDegraded("runner exited: device kernel image is invalid");

        var decision = policy.Decide(new GpuReading(GameSignals.None, 9L << 30, 1));

        Assert.NotEqual(LlmRoute.Gpu, decision.Route);
        Assert.Contains("degraded", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GpuDegradedSessionUsesTheCpuRouteWhenTheUserOptedIntoIt()
    {
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.UseCpuLlm);
        policy.MarkGpuDegraded("runner crashed");

        Assert.Equal(LlmRoute.Cpu, policy.Decide(new GpuReading(GameSignals.None, 9L << 30, 1)).Route);
    }

    [Fact]
    public void IndividualSignalsCanBeTurnedOffInSettings()
    {
        // Each sensor has a documented false-positive mode -- a video player covering the monitor,
        // for instance -- so each is individually disableable rather than the whole governor.
        var settings = Defaults with { TrustFullscreenGeometry = false };
        var policy = new RoutingPolicy(settings, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(GameSignals.FullscreenGeometry, 9L << 30, 2));

        Assert.Equal(LlmRoute.Gpu, decision.Route);
        Assert.Equal(GameSignals.None, decision.Signals);
    }

    [Fact]
    public void EveryDecisionCarriesTheInputsThatProducedIt()
    {
        // `bench -- route` prints these. A routing decision no one can check is a routing
        // decision no one can debug.
        var policy = new RoutingPolicy(Defaults, InGameBehaviour.SkipLlm);

        var decision = policy.Decide(new GpuReading(
            GameSignals.NotificationState | GameSignals.GpuBusy,
            FreeVramBytes: 1L << 30,
            GpuUtilisationPercent: 91,
            ForegroundProcess: "cyberpunk2077"));

        Assert.Equal(1L << 30, decision.FreeVramBytes);
        Assert.Equal(91u, decision.GpuUtilisationPercent);
        Assert.Equal("cyberpunk2077", decision.ForegroundProcess);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }
}
