using System.Globalization;
using Jane.Core.Abstractions;
using Jane.Core.Settings;

namespace Jane.Core.Gpu;

/// <param name="Signals">Raw signal flags as observed, before settings filter them.</param>
/// <param name="FreeVramBytes">Null when NVML is unavailable, which is a warning, not a fault.</param>
public sealed record GpuReading(
    GameSignals Signals,
    long? FreeVramBytes,
    uint? GpuUtilisationPercent,
    string? ForegroundProcess = null);

/// <summary>
/// Turns a GPU reading into a route.
/// </summary>
/// <remarks>
/// Deliberately separate from the sensors, and deliberately in <c>Jane.Core</c> with no Win32:
/// the policy is where the user's hard constraint lives, and it has to be assertable without
/// launching a game. <see cref="IGpuGovernor"/>'s Windows implementation supplies the readings.
/// </remarks>
public sealed class RoutingPolicy(GpuSettings settings, InGameBehaviour inGame, bool llmEnabled = true)
{
    private string? _degradedReason;

    /// <summary>Set when a GPU request took the server down. Sticky for the session.</summary>
    /// <remarks>
    /// The open sm_120 MMQ crash (`ollama#14374`) is quant-dependent and does not heal. Retrying
    /// the GPU on the next dictation would mean crashing the server on the next dictation.
    /// </remarks>
    public bool IsGpuDegraded => _degradedReason is not null;

    public void MarkGpuDegraded(string reason) => _degradedReason ??= reason;

    public RoutingDecision Decide(GpuReading reading)
    {
        var signals = Filter(reading);

        if (!llmEnabled)
        {
            return new RoutingDecision(LlmRoute.Skip, signals,
                "Formatting is disabled in settings; raw speech-to-text is injected.",
                reading.FreeVramBytes, reading.GpuUtilisationPercent, reading.ForegroundProcess);
        }

        if (signals != GameSignals.None)
        {
            var route = inGame == InGameBehaviour.UseCpuLlm ? LlmRoute.Cpu : LlmRoute.Skip;
            var what = route == LlmRoute.Cpu
                ? "formatting is routed to the CPU model (opt-in)"
                : "the LLM is skipped and raw speech-to-text is injected";

            return new RoutingDecision(route, signals,
                $"A game or fullscreen app is running ({Describe(signals)}), so {what}.",
                reading.FreeVramBytes, reading.GpuUtilisationPercent, reading.ForegroundProcess);
        }

        if (IsGpuDegraded)
        {
            var route = inGame == InGameBehaviour.UseCpuLlm ? LlmRoute.Cpu : LlmRoute.Skip;
            return new RoutingDecision(route, signals,
                $"The GPU route is degraded for this session ({_degradedReason}); " +
                (route == LlmRoute.Cpu ? "using the CPU model." : "raw speech-to-text is injected."),
                reading.FreeVramBytes, reading.GpuUtilisationPercent, reading.ForegroundProcess);
        }

        return new RoutingDecision(LlmRoute.Gpu, GameSignals.None,
            reading.FreeVramBytes is { } free
                ? $"GPU is free ({free / (1024.0 * 1024 * 1024):F1} GB VRAM available, {reading.GpuUtilisationPercent ?? 0}% utilised)."
                : "No game detected and no GPU telemetry available; using the GPU model.",
            reading.FreeVramBytes, reading.GpuUtilisationPercent, reading.ForegroundProcess);
    }

    /// <summary>
    /// Applies the per-signal trust settings and derives <see cref="GameSignals.GpuBusy"/> from the
    /// VRAM and utilisation thresholds.
    /// </summary>
    /// <remarks>
    /// Each sensor has a documented false positive -- a video player covering the monitor looks
    /// exactly like a borderless game to the geometry check -- so each is individually disableable.
    /// Disabling the whole governor instead would be a bigger hammer than the problem.
    /// </remarks>
    private GameSignals Filter(GpuReading reading)
    {
        var signals = GameSignals.None;

        if (settings.TrustNotificationState && reading.Signals.HasFlag(GameSignals.NotificationState))
        {
            signals |= GameSignals.NotificationState;
        }

        if (settings.TrustFullscreenGeometry && reading.Signals.HasFlag(GameSignals.FullscreenGeometry))
        {
            signals |= GameSignals.FullscreenGeometry;
        }

        if (!settings.TrustNvml)
        {
            return signals;
        }

        // Absent telemetry is not evidence of a busy GPU. Treating it as such would silently
        // disable formatting on any machine whose driver does not ship a readable NVML.
        var busy = reading.Signals.HasFlag(GameSignals.GpuBusy)
                   || (reading.FreeVramBytes is { } free && free < settings.MinimumFreeVramBytes)
                   || (reading.GpuUtilisationPercent is { } used && used >= settings.BusyUtilisationPercent);

        return busy ? signals | GameSignals.GpuBusy : signals;
    }

    private static string Describe(GameSignals signals)
    {
        var parts = new List<string>(3);
        if (signals.HasFlag(GameSignals.NotificationState))
        {
            parts.Add("fullscreen app reported by the shell");
        }

        if (signals.HasFlag(GameSignals.FullscreenGeometry))
        {
            parts.Add("foreground window covers the monitor");
        }

        if (signals.HasFlag(GameSignals.GpuBusy))
        {
            parts.Add("GPU busy or low on free VRAM");
        }

        return string.Join(", ", parts);
    }

    public string DescribeThresholds() => string.Create(CultureInfo.InvariantCulture,
        $"free VRAM below {settings.MinimumFreeVramBytes / (1024.0 * 1024 * 1024):F1} GB or utilisation at or above {settings.BusyUtilisationPercent}% counts as busy");
}
