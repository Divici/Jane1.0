using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Core.Gpu;
using Jane.Core.Settings;

namespace Jane.Windows.Gpu;

/// <summary>
/// The live governor: reads all three signals and hands them to the routing policy.
/// </summary>
/// <remarks>
/// Readings are cached for a short window because the governor is consulted on every key-down and
/// again when CPU-bound work wants to know whether to drop priority. NVML and
/// <c>SHQueryUserNotificationState</c> are cheap but not free, and a game does not start and stop
/// between two calls a few hundred milliseconds apart.
/// </remarks>
public sealed class GpuGovernor : IGpuGovernor, IDisposable
{
    /// <summary>
    /// Long enough that repeated calls within one dictation are free, short enough that alt-tabbing
    /// out of a game is picked up before the next dictation.
    /// </summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromMilliseconds(750);

    private readonly FullscreenDetector _fullscreen = new();
    private readonly NvmlInterop _nvml = new();
    private readonly Lock _gate = new();
    private readonly bool _nvmlReady;

    private RoutingDecision? _cached;
    private long _cachedAt;
    private bool _disposed;

    public GpuGovernor(GpuSettings settings, InGameBehaviour inGame, bool llmEnabled = true)
    {
        Policy = new RoutingPolicy(settings, inGame, llmEnabled);
        _nvmlReady = _nvml.TryInitialise(out _);
    }

    public RoutingPolicy Policy { get; }

    public bool IsGameRunning => Decide().GameDetected;

    public RoutingDecision Decide()
    {
        lock (_gate)
        {
            if (_cached is not null && Stopwatch.GetElapsedTime(_cachedAt) < CacheWindow)
            {
                return _cached;
            }

            _cached = Policy.Decide(Read());
            _cachedAt = Stopwatch.GetTimestamp();
            return _cached;
        }
    }

    /// <summary>Forces the next <see cref="Decide"/> to re-read the sensors.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private GpuReading Read()
    {
        var (signals, foreground) = _fullscreen.Detect();

        if (!_nvmlReady)
        {
            return new GpuReading(signals, null, null, foreground);
        }

        var gpus = _nvml.Snapshot();
        if (gpus.Count == 0)
        {
            return new GpuReading(signals, null, null, foreground);
        }

        // The first device is the one Ollama will load onto. Jane does not try to place work on a
        // second GPU: the routing question is "is the machine busy", not "which card is free".
        var primary = gpus[0];
        return new GpuReading(
            signals,
            (long)primary.FreeBytes,
            primary.GpuUtilisationPercent,
            foreground);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nvml.Dispose();
    }
}
