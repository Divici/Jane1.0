using System.Text.Json.Serialization;

namespace Jane.Core.Diagnostics;

/// <summary>Outcome of a single environment or capability probe.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeStatus>))]
public enum ProbeStatus
{
    /// <summary>The probe could not run at all -- a dependency it needs was itself absent.</summary>
    Skipped,

    /// <summary>Everything the probe checked is as Jane needs it.</summary>
    Pass,

    /// <summary>Jane works, but in a reduced shape. Never blocks startup.</summary>
    Warn,

    /// <summary>Jane cannot do its job until this is fixed.</summary>
    Fail,
}

/// <summary>Aggregate verdict over a whole <see cref="DoctorReport"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DoctorVerdict>))]
public enum DoctorVerdict
{
    Pass,
    Warn,
    Fail,
}

/// <param name="Name">Stable dotted identifier, e.g. <c>ollama.num_gpu_zero</c>. Reports are diffed across runs on this.</param>
/// <param name="Status">What the probe concluded.</param>
/// <param name="Detail">What was actually observed. Written for a human reading the report.</param>
/// <param name="Remedy">
/// What to do about it. Required whenever <paramref name="Status"/> is <see cref="ProbeStatus.Fail"/>
/// or <see cref="ProbeStatus.Warn"/> -- a verdict with no remedy tells the user nothing actionable.
/// </param>
/// <param name="Data">Raw values behind the verdict (VRAM bytes, port owners, measured latencies).</param>
/// <param name="Elapsed">How long the probe took. A slow probe is itself a finding.</param>
public sealed record ProbeResult(
    string Name,
    ProbeStatus Status,
    string Detail,
    string? Remedy = null,
    IReadOnlyDictionary<string, string>? Data = null,
    TimeSpan Elapsed = default);

/// <summary>A single environment or capability check. Implementations must not throw for an
/// expected negative -- returning a <see cref="ProbeStatus.Fail"/> with a remedy is the
/// contract. <see cref="Doctor"/> converts an unexpected throw into a Fail so one broken probe
/// cannot cost the user every other verdict.</summary>
public interface IProbe
{
    string Name { get; }

    Task<ProbeResult> RunAsync(CancellationToken cancellationToken);
}

/// <param name="Verdict">Worst status across all probes: any Fail is a Fail, else any Warn is a Warn.</param>
/// <param name="Results">In probe declaration order, so the report reads predictably run to run.</param>
public sealed record DoctorReport(
    DateTimeOffset GeneratedAt,
    DoctorVerdict Verdict,
    IReadOnlyList<ProbeResult> Results)
{
    public int CountOf(ProbeStatus status) => Results.Count(r => r.Status == status);
}
