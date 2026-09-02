using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jane.Bench.Commands;

/// <summary>Latency percentiles over a set of warm runs.</summary>
public sealed record LatencyStats(double P50Ms, double P95Ms, double MinMs, double MaxMs, int Samples)
{
    public static LatencyStats From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return new LatencyStats(0, 0, 0, 0, 0);
        }

        var sorted = samples.Order().ToArray();
        return new LatencyStats(
            P50Ms: Percentile(sorted, 0.50),
            P95Ms: Percentile(sorted, 0.95),
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            Samples: sorted.Length);
    }

    /// <summary>
    /// Nearest-rank percentile. With the small sample counts a bench like this can afford,
    /// interpolation invents precision the measurement does not have.
    /// </summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <param name="UtteranceSeconds">1 s and 10 s, because short-utterance latency has fixed overhead a long-form median hides.</param>
/// <param name="ColdInitMs">Session init from a fresh engine instance. The number that decides whether the model must stay resident.</param>
/// <param name="FirstInferenceMs">The first transcription after that init, with nothing warmed.</param>
/// <param name="ColdPathMs">Cold init plus first inference: what a load-on-demand design would charge the user per dictation.</param>
public sealed record BenchMeasurement(
    string EngineId,
    string DecodingMode,
    double UtteranceSeconds,
    int NumThreads,
    double ColdInitMs,
    double FirstInferenceMs,
    double ColdPathMs,
    LatencyStats Warm,
    double RealTimeFactor,
    double WordErrorRate,
    int FixtureCount,
    string? Note = null);

/// <param name="Reason">Why this engine won, in a sentence a person can check against the rows.</param>
public sealed record BenchSelection(
    string EngineId,
    int NumThreads,
    bool HotwordBiasingEnabled,
    string Reason);

/// <param name="Gates">Named thresholds and whether the measurement met them. Recorded whether or not they passed.</param>
public sealed record BenchReport(
    DateTimeOffset GeneratedAt,
    string Machine,
    IReadOnlyList<BenchMeasurement> Measurements,
    BenchSelection Selection,
    IReadOnlyList<BenchGate> Gates)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}

/// <param name="BudgetMs">The threshold as the plan states it.</param>
/// <param name="MeasuredMs">What the machine actually did.</param>
public sealed record BenchGate(string Name, double BudgetMs, double MeasuredMs, bool Passed, string Detail);
