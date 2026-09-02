using Jane.Core.Diagnostics;
using Jane.Core.Diagnostics.Probes;
using Jane.Llm;
using Jane.Llm.Diagnostics;
using Jane.Speech.Diagnostics;
using Jane.Windows.Diagnostics;

namespace Jane.Bench.Commands;

/// <summary>
/// Answers the environment questions the rest of the build depends on, and writes them to
/// <c>doctor-report.json</c> so a verdict can be diffed run to run.
/// </summary>
/// <remarks>
/// The Ollama capability probes need a live server, so <c>doctor</c> starts and supervises its
/// own -- the same standalone binary, the same non-default port, the same Job Object -- and
/// tears it down on exit. It adopts an already-running server rather than fighting for the port.
/// </remarks>
public sealed class DoctorCommand(JaneEnvironment environment)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var options = new OllamaSupervisorOptions(environment.OllamaExe, environment.OllamaHost);
        await using var supervisor = new OllamaSupervisor(options);

        using var http = new HttpClient
        {
            BaseAddress = new Uri(environment.OllamaBaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };
        var client = new OllamaChatClient(http);

        var startedByUs = false;
        string? startupError = null;
        if (File.Exists(environment.OllamaExe))
        {
            try
            {
                startedByUs = await supervisor.EnsureRunningAsync(client, cancellationToken);
            }
            catch (OllamaException ex)
            {
                // Reported as a probe verdict rather than crashing the run: every non-Ollama
                // probe still has something useful to say.
                startupError = ex.Message;
            }
        }

        var doctor = new Doctor(BuildProbes(client));
        var report = await doctor.RunAsync(cancellationToken);

        if (startupError is not null)
        {
            report = report with
            {
                Results = [.. report.Results, new ProbeResult(
                    "ollama.supervisor",
                    ProbeStatus.Fail,
                    startupError,
                    Remedy: "Check that nothing else owns the port, then re-run. doctor adopts an existing server if one is already answering.")],
                Verdict = DoctorVerdict.Fail,
            };
        }

        Print(report, startedByUs, supervisor);

        var path = Path.Combine(environment.RepoRoot, "doctor-report.json");
        await DoctorReportWriter.WriteAsync(report, path, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"Report written to {path}");

        return report.Verdict == DoctorVerdict.Fail ? 1 : 0;
    }

    private IReadOnlyList<IProbe> BuildProbes(OllamaChatClient client) =>
    [
        new MicrophoneProbe(),
        new GpuProbe(),
        new DiskSpaceProbe(environment.Paths),
        new ModelDirectoryProbe(environment.Paths),
        new SherpaNativeProbe(),
        new OllamaBinaryProbe(environment.OllamaExe),
        new PortOwnerProbe(),
        new OllamaReachableProbe(client, environment.OllamaBaseUrl),

        // The two probes that decide Phase 6's shape: num_gpu:0 collapsing two servers into one
        // (recorded as plan.md P0-1) and the quant sweep against the sm_120 MMQ crash.
        new CpuPinningProbe(client, environment.CpuModel),
        new GpuInferenceProbe(client, [environment.GpuModel, environment.CpuModel]),
    ];

    private static void Print(DoctorReport report, bool startedByUs, OllamaSupervisor supervisor)
    {
        Console.WriteLine();
        Console.WriteLine("  Jane doctor");
        Console.WriteLine("  -----------");
        Console.WriteLine(startedByUs
            ? $"  Started a supervised ollama.exe (pid {supervisor.ChildProcessId}) on {supervisor.BaseUrl}."
            : $"  Adopted an Ollama server already answering on {supervisor.BaseUrl}.");
        Console.WriteLine();

        foreach (var result in report.Results)
        {
            var tag = result.Status switch
            {
                ProbeStatus.Pass => "PASS",
                ProbeStatus.Warn => "WARN",
                ProbeStatus.Fail => "FAIL",
                _ => "SKIP",
            };

            Console.WriteLine($"  [{tag}] {result.Name,-26} {result.Detail}");
            if (result.Remedy is { Length: > 0 } remedy)
            {
                Console.WriteLine($"           -> {remedy}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  Verdict: {report.Verdict.ToString().ToUpperInvariant()}  " +
            $"({report.CountOf(ProbeStatus.Pass)} pass, " +
            $"{report.CountOf(ProbeStatus.Warn)} warn, " +
            $"{report.CountOf(ProbeStatus.Fail)} fail)");
    }
}
