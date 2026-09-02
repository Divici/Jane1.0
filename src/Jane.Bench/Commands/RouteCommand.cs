using System.Globalization;
using Jane.Core.Abstractions;
using Jane.Core.Platform;
using Jane.Core.Settings;
using Jane.Windows.Gpu;

namespace Jane.Bench.Commands;

/// <summary>
/// Prints the routing decision and every input that produced it.
/// </summary>
/// <remarks>
/// The governor decides silently, on every key-down, whether Jane is allowed to touch the GPU.
/// That is exactly the kind of decision that becomes folklore unless it can be interrogated, so
/// this command exists to answer "why did it do that" with numbers rather than a shrug.
/// </remarks>
public sealed class RouteCommand(JaneEnvironment environment)
{
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var store = new SettingsStore(environment.Paths);
        var settings = store.Read();

        using var governor = new GpuGovernor(settings.Gpu, settings.Llm.InGame, settings.Llm.Enabled);
        var decision = governor.Decide();

        Console.WriteLine();
        Console.WriteLine("  Jane route");
        Console.WriteLine("  ----------");
        Console.WriteLine();
        Console.WriteLine($"  Route:      {Describe(decision.Route)}");
        Console.WriteLine($"  Reason:     {decision.Reason}");
        Console.WriteLine();
        Console.WriteLine("  Signals");
        Console.WriteLine($"    shell reports fullscreen      {Mark(decision.Signals.HasFlag(GameSignals.NotificationState))}" +
                          $"   (trusted: {settings.Gpu.TrustNotificationState})");
        Console.WriteLine($"    foreground covers monitor     {Mark(decision.Signals.HasFlag(GameSignals.FullscreenGeometry))}" +
                          $"   (trusted: {settings.Gpu.TrustFullscreenGeometry})");
        Console.WriteLine($"    GPU busy / low free VRAM      {Mark(decision.Signals.HasFlag(GameSignals.GpuBusy))}" +
                          $"   (trusted: {settings.Gpu.TrustNvml})");
        Console.WriteLine();
        Console.WriteLine("  Inputs");
        Console.WriteLine($"    foreground process            {decision.ForegroundProcess ?? "(unknown)"}");
        Console.WriteLine($"    free VRAM                     {FormatVram(decision.FreeVramBytes)}");
        Console.WriteLine($"    GPU utilisation               {(decision.GpuUtilisationPercent is { } u ? u + "%" : "(no NVML)")}");
        Console.WriteLine($"    thresholds                    {governor.Policy.DescribeThresholds()}");
        Console.WriteLine();
        Console.WriteLine("  Settings");
        Console.WriteLine($"    in-game behaviour             {settings.Llm.InGame}");
        Console.WriteLine($"    LLM enabled                   {settings.Llm.Enabled}");
        Console.WriteLine($"    GPU model                     {settings.Llm.GpuModel}");
        Console.WriteLine($"    CPU model                     {settings.Llm.CpuModel}");
        Console.WriteLine();

        return Task.FromResult(0);
    }

    private static string Describe(LlmRoute route) => route switch
    {
        LlmRoute.Skip => "LLM-OFF  (raw speech-to-text is injected)",
        LlmRoute.Gpu => "GPU      (jane-qwen3-4b)",
        LlmRoute.Cpu => "CPU      (jane-qwen3-1.7b, num_gpu=0)",
        _ => route.ToString(),
    };

    private static string Mark(bool fired) => fired ? "FIRED    " : "clear    ";

    private static string FormatVram(long? bytes) => bytes is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value / (1024.0 * 1024 * 1024):F2} GB")
        : "(no NVML)";
}
