using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Jane.Core.Diagnostics;

namespace Jane.Core.Tests;

public sealed class DoctorTests
{
    private static IProbe Probe(string name, ProbeStatus status, string? remedy = null) =>
        new StubProbe(name, ct => Task.FromResult(new ProbeResult(name, status, $"{name} said {status}", remedy)));

    [Fact]
    public async Task UnreachableOllama_FailsWithARemedy()
    {
        // The remedy string is the whole point of the doctor: a bare "FAIL" tells the user
        // nothing they can act on.
        var doctor = new Doctor([
            Probe("ollama.reachable", ProbeStatus.Fail, "Run build/get-ollama.ps1, then start Jane."),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorVerdict.Fail, report.Verdict);
        var result = Assert.Single(report.Results);
        Assert.Equal(ProbeStatus.Fail, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
    }

    [Fact]
    public async Task HealthyMachine_Passes()
    {
        var doctor = new Doctor([
            Probe("microphone", ProbeStatus.Pass),
            Probe("gpu", ProbeStatus.Pass),
            Probe("disk", ProbeStatus.Pass),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorVerdict.Pass, report.Verdict);
        Assert.All(report.Results, r => Assert.Equal(ProbeStatus.Pass, r.Status));
    }

    [Fact]
    public async Task WarnDoesNotFailTheVerdict_ButIsVisible()
    {
        // A missing GPU is a warning, not a failure -- Jane runs ASR on the CPU by design and
        // simply skips the LLM. Conflating the two would block a machine that works fine.
        var doctor = new Doctor([
            Probe("microphone", ProbeStatus.Pass),
            Probe("gpu", ProbeStatus.Warn, "No NVIDIA GPU found; the LLM formatting pass will be skipped."),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorVerdict.Warn, report.Verdict);
    }

    [Fact]
    public async Task ThrowingProbe_BecomesAFailResultRatherThanCrashingTheRun()
    {
        // One broken probe must not cost the user every other verdict in the report.
        var doctor = new Doctor([
            new StubProbe("explodes", _ => throw new InvalidOperationException("nvml.dll is missing")),
            Probe("microphone", ProbeStatus.Pass),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Results.Count);
        var failed = report.Results.Single(r => r.Name == "explodes");
        Assert.Equal(ProbeStatus.Fail, failed.Status);
        Assert.Contains("nvml.dll is missing", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(ProbeStatus.Pass, report.Results.Single(r => r.Name == "microphone").Status);
    }

    [Fact]
    public async Task EveryProbeGetsAVerdict_EvenWhenOneIsSlow()
    {
        var doctor = new Doctor([
            new StubProbe("slow", async ct =>
            {
                await Task.Delay(20, ct);
                return new ProbeResult("slow", ProbeStatus.Pass, "eventually", null);
            }),
            Probe("fast", ProbeStatus.Pass),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        // Results keep probe declaration order, so the report always reads in the order the
        // user was told to expect, however long each probe took.
        Assert.Equal(["slow", "fast"], report.Results.Select(r => r.Name).ToArray());
        Assert.All(report.Results, r => Assert.Equal(ProbeStatus.Pass, r.Status));
    }

    [Fact]
    public async Task ReportSerialisesToDoctorReportJson_AndRoundTrips()
    {
        var doctor = new Doctor([
            new StubProbe("ollama.num_gpu_zero", _ => Task.FromResult(new ProbeResult(
                "ollama.num_gpu_zero",
                ProbeStatus.Pass,
                "options.num_gpu=0 pinned the model to CPU",
                Remedy: null,
                Data: new Dictionary<string, string> { ["size_vram"] = "0" }))),
        ]);

        var report = await doctor.RunAsync(TestContext.Current.CancellationToken);

        var dir = Directory.CreateTempSubdirectory("jane-doctor-test");
        try
        {
            var path = Path.Combine(dir.FullName, "doctor-report.json");
            await DoctorReportWriter.WriteAsync(report, path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var round = JsonSerializer.Deserialize<DoctorReport>(json, DoctorReportWriter.JsonOptions);

            Assert.NotNull(round);
            Assert.Equal(DoctorVerdict.Pass, round.Verdict);
            var probe = Assert.Single(round.Results);
            Assert.Equal("ollama.num_gpu_zero", probe.Name);
            Assert.Equal("0", probe.Data!["size_vram"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void JaneCore_HasNoWindowsTargetFramework()
    {
        // Jane.Core holds the orchestrator and every policy decision. If it ever picks up the
        // Windows TFM, those become untestable without a desktop session -- and the CI-shaped
        // parts of this suite stop being able to run at all.
        var tfm = typeof(Doctor).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(tfm);
        Assert.Equal(".NETCoreApp,Version=v10.0", tfm.FrameworkName);
        Assert.DoesNotContain("Windows", tfm.FrameworkName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JaneCore_ReferencesNoWindowsDesktopAssembly()
    {
        string[] forbidden =
        [
            "WindowsBase", "PresentationCore", "PresentationFramework",
            "System.Windows.Forms", "System.Drawing.Common", "Microsoft.Win32.Registry",
        ];

        var referenced = typeof(Doctor).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, referenced, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class StubProbe(string name, Func<CancellationToken, Task<ProbeResult>> run) : IProbe
    {
        public string Name => name;

        public Task<ProbeResult> RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }
}
