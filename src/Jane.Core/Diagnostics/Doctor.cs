using System.Diagnostics;

namespace Jane.Core.Diagnostics;

/// <summary>
/// Runs every registered probe and aggregates a verdict.
/// </summary>
/// <remarks>
/// Probes run sequentially, not in parallel. Several of them contend for the same scarce
/// resource -- the GPU, the audio device, a single Ollama child process -- and running them
/// concurrently would make their own measurements wrong. The whole suite is a few seconds.
/// </remarks>
public sealed class Doctor(IReadOnlyList<IProbe> probes)
{
    public IReadOnlyList<IProbe> Probes { get; } = probes;

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>(Probes.Count);

        foreach (var probe in Probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOneAsync(probe, cancellationToken));
        }

        return new DoctorReport(DateTimeOffset.Now, Aggregate(results), results);
    }

    private static async Task<ProbeResult> RunOneAsync(IProbe probe, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await probe.RunAsync(cancellationToken);
            stopwatch.Stop();

            // Probes are not required to time themselves; fill it in when they did not.
            return result.Elapsed == default ? result with { Elapsed = stopwatch.Elapsed } : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // An unexpected throw is a real finding, not a reason to abandon the run. The
            // message goes into Detail verbatim so the report is diagnosable without a log.
            return new ProbeResult(
                probe.Name,
                ProbeStatus.Fail,
                $"Probe threw {ex.GetType().Name}: {ex.Message}",
                Remedy: "This is a bug in the probe or a broken dependency. Re-run `doctor` with JANE_LOG_LEVEL=debug and attach doctor-report.json.",
                Elapsed: stopwatch.Elapsed);
        }
    }

    private static DoctorVerdict Aggregate(IEnumerable<ProbeResult> results)
    {
        var worst = DoctorVerdict.Pass;
        foreach (var result in results)
        {
            switch (result.Status)
            {
                case ProbeStatus.Fail:
                    return DoctorVerdict.Fail;
                case ProbeStatus.Warn:
                    worst = DoctorVerdict.Warn;
                    break;
                case ProbeStatus.Pass:
                case ProbeStatus.Skipped:
                default:
                    break;
            }
        }

        return worst;
    }
}
