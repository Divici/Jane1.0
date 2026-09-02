using System.Diagnostics;
using System.Globalization;

namespace Jane.App.Tests;

/// <summary>
/// Jane's whole value proposition is that it sits there costing nothing until you speak.
/// </summary>
/// <remarks>
/// The user's constraint was explicit -- "still able to game or use all of my computer's
/// resources fully and freely at all times" -- so idle cost is a product requirement, not a
/// nicety. The resident ASR model's ~2 GB is accounted separately and disclosed in settings; it
/// holds no CPU and no VRAM, and Phase 4 loads none of it.
/// <para>
/// A real Jane process is started and measured, rather than the test host standing in for one.
/// The test host carries xUnit, every other fixture in this assembly and their retained visuals,
/// and measured 177 MB against Jane's own 94 MB -- so an in-process reading would fail a budget
/// the product comfortably meets, and would keep failing for reasons that have nothing to do
/// with Jane. This also smoke-tests that the tray app starts and stays up at all.
/// </para>
/// </remarks>
public sealed class IdleFootprintTests
{
    /// <summary>Long enough that a stray timer or a leaked animation clock shows up.</summary>
    private const int DefaultSampleSeconds = 60;

    /// <summary>Shortens the window for a quick local run. The default stays 60 s.</summary>
    private const string SampleSecondsVariable = "JANE_IDLE_SAMPLE_SECONDS";

    private const double MaxCpuPercent = 1.0;
    private const long MaxWorkingSetBytes = 150L * 1024 * 1024;

    [Fact]
    public async Task IdleCpuUnder1PctWorkingSetUnder150MB()
    {
        var token = TestContext.Current.CancellationToken;

        var assembly = FindAppAssembly();
        Assert.SkipWhen(assembly is null, "Jane.App has not been built, so there is no process to measure.");

        using var app = Launch(assembly!);

        try
        {
            // Give WPF, the shell registration and the JIT time to finish before the clock starts.
            await Task.Delay(TimeSpan.FromSeconds(6), token);
            Assert.False(app.HasExited, $"Jane exited during startup with code {ExitCodeOrZero(app)}.");

            var (cpuPercent, peakWorkingSet) = await SampleAsync(app, SampleWindow(), token);

            Assert.True(
                cpuPercent < MaxCpuPercent,
                $"Idle CPU was {cpuPercent:F3}% of the machine, over the {MaxCpuPercent}% budget.");

            Assert.True(
                peakWorkingSet < MaxWorkingSetBytes,
                $"Idle working set peaked at {peakWorkingSet / (1024 * 1024)} MB, over the {MaxWorkingSetBytes / (1024 * 1024)} MB budget.");
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
            }
        }
    }

    /// <remarks>
    /// Through the shared <c>dotnet</c> host rather than Jane.exe. The apphost's manifest asks
    /// for <c>uiAccess="true"</c>, which Windows refuses to grant -- and refuses to launch at all
    /// -- until Phase 13 signs the binary and installs it under %ProgramFiles%. Same assembly,
    /// same WPF, same tray icon; only the launcher differs.
    /// </remarks>
    private static Process Launch(string assemblyPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
        };
        startInfo.ArgumentList.Add(assemblyPath);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Jane process.");
    }

    private static async Task<(double CpuPercent, long PeakWorkingSet)> SampleAsync(
        Process app,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        // TotalProcessorTime rather than a PerformanceCounter: it is the same kernel counter
        // without a System.Diagnostics.PerformanceCounter dependency, and it is cumulative rather
        // than sampled, so a short burst cannot slip between two readings.
        app.Refresh();
        var cpuAtStart = app.TotalProcessorTime;
        var peak = app.WorkingSet64;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < window)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            if (app.HasExited)
            {
                Assert.Fail($"Jane exited while idle with code {ExitCodeOrZero(app)}.");
            }

            app.Refresh();
            peak = Math.Max(peak, app.WorkingSet64);
        }

        clock.Stop();
        app.Refresh();

        var cpuSeconds = (app.TotalProcessorTime - cpuAtStart).TotalSeconds;

        // Normalised across cores, the way Task Manager reports it: 1% here means one percent of
        // the whole machine, not one percent of one core.
        var percent = cpuSeconds / (clock.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100;

        return (percent, peak);
    }

    private static int ExitCodeOrZero(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The freshest built Jane.dll that has a runtimeconfig beside it, so the dotnet host can
    /// actually run it. Found by search rather than by a hardcoded path, because the build output
    /// lands under bin/Debug or bin/x64/Debug depending on how the solution was built.
    /// </summary>
    private static string? FindAppAssembly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Jane.sln")))
        {
            root = root.Parent;
        }

        var binaries = root is null ? null : Path.Combine(root.FullName, "src", "Jane.App", "bin");
        if (binaries is null || !Directory.Exists(binaries))
        {
            return null;
        }

        return Directory.EnumerateFiles(binaries, "Jane.dll", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static TimeSpan SampleWindow()
    {
        var configured = Environment.GetEnvironmentVariable(SampleSecondsVariable);

        return double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(DefaultSampleSeconds);
    }
}
