using System.Text.Json;
using System.Text.Json.Serialization;
using Jane.Core.Abstractions;

namespace Jane.Bench.Commands;

/// <param name="ColdMs">First measurement after a fresh engine. Reported apart from warm, never averaged into it.</param>
public sealed record StageLatency(string Stage, double P50Ms, double P95Ms, double ColdMs, int Samples)
{
    public static StageLatency From(string stage, IReadOnlyList<double> warm, double cold)
    {
        if (warm.Count == 0)
        {
            return new StageLatency(stage, 0, 0, cold, 0);
        }

        var sorted = warm.Order().ToArray();
        return new StageLatency(
            stage,
            sorted[Math.Clamp((int)Math.Ceiling(0.50 * sorted.Length) - 1, 0, sorted.Length - 1)],
            sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)],
            cold,
            sorted.Length);
    }
}

/// <param name="FalseBypassRate">
/// How often the bypass fired on a transcript the LLM would have changed. The number BLOCKER #9
/// exists to keep honest -- a bypass heuristic nobody measures is a bypass heuristic that quietly
/// skips the user's own settings.
/// </param>
public sealed record AccuracyMetrics(
    double WordErrorRate,
    double PunctuationAccuracy,
    double CasingAccuracy,
    double TermRecall,
    double FalseBypassRate,
    int BypassedCount,
    int FixtureCount);

/// <param name="Route">GPU, in-game LLM-off, or the opt-in CPU route. All three are measured.</param>
public sealed record RouteResult(
    LlmRoute Route,
    AccuracyMetrics Accuracy,
    IReadOnlyList<StageLatency> Stages,
    double TotalP50Ms,
    double TotalP95Ms,
    double TotalColdMs,
    string? Note = null);

/// <param name="Tolerance">How far past the budget a measurement may drift before it counts as a regression.</param>
public sealed record EvalGate(string Name, double BudgetMs, double MeasuredMs, double Tolerance, bool Passed, string Detail);

public sealed record EvalReport(
    DateTimeOffset GeneratedAt,
    string EngineId,
    IReadOnlyList<RouteResult> Routes,
    IReadOnlyList<EvalGate> Gates,
    double StartupMs)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<LlmRoute>() },
    };

    public bool Passed => Gates.All(g => g.Passed);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this, JsonOptions), cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task<EvalReport?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<EvalReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
