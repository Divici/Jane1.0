using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Jane.Bench.Fixtures;
using Jane.Core.Abstractions;
using Jane.Core.Audio;
using Jane.Core.Settings;
using Jane.Core.Text;
using Jane.Speech;

namespace Jane.Bench.Commands;

/// <summary>
/// Measures every ASR configuration Jane could ship and picks one, with no interactive input.
/// </summary>
/// <remarks>
/// The user asked not to run a bench-off by hand, so this has to decide on its own and be
/// defensible afterwards -- hence a report with every row that fed the choice, not just a winner.
///
/// The axes are the ones the plan names, and each exists because guessing it wrong is a specific
/// documented failure:
/// <list type="bullet">
/// <item>engine and quantisation, because Parakeet's CPU figure is an extrapolation from a
/// phone-class ARM core, not a measurement on this machine;</item>
/// <item>1 s and 10 s utterances, because short-utterance latency carries fixed overhead that a
/// long-form median hides, and dictation is mostly short;</item>
/// <item>cold init separately from warm inference, because the original design made the cold path
/// the everyday path and a warm-only average would never have shown it;</item>
/// <item>greedy against hotword-biased decoding, because biasing forces beam search and could
/// double ASR latency -- which would make Deep Context and the dictionary a net loss.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BenchCommand(JaneEnvironment environment)
{
    /// <summary>
    /// Cold init plus a 1 s utterance. The plan states 1.5 s. Measured on this machine it lands
    /// at roughly that, so the gate is recorded honestly in the report rather than tuned to pass;
    /// see the Phase 1 note in plan.md for why the shipped mitigation is preloading at startup.
    /// </summary>
    public const double ColdPathBudgetMs = 1500;

    /// <summary>
    /// What a resident engine costs per dictation for a short utterance. This is the number the
    /// user actually feels, because the model is loaded before the tray icon appears.
    /// </summary>
    public const double WarmShortBudgetMs = 300;

    /// <summary>
    /// Biasing may cost latency but must not cost this much: past it, Deep Context and the
    /// dictionary make dictation worse overall and biasing stays off.
    /// </summary>
    public const double BiasingOverheadBudgetRatio = 1.75;

    private const int WarmRuns = 7;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var fixtures = await EnsureFixturesAsync(cancellationToken);
        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("No fixtures could be built; cannot bench.");
            return 1;
        }

        var oneSecond = BuildUtterance(fixtures, targetSeconds: 1);
        var tenSecond = BuildUtterance(fixtures, targetSeconds: 10);

        Console.WriteLine();
        Console.WriteLine("  Jane bench");
        Console.WriteLine("  ----------");
        Console.WriteLine($"  {fixtures.Count} fixtures; 1 s utterance = {oneSecond.Length / (double)AudioFormat.SampleRate:F2} s, " +
                          $"10 s utterance = {tenSecond.Length / (double)AudioFormat.SampleRate:F2} s");
        Console.WriteLine();

        var measurements = new List<BenchMeasurement>();

        foreach (var threads in (int[])[2, 4, 8])
        {
            measurements.Add(await MeasureParakeetAsync(fixtures, oneSecond, threads, biasing: false, cancellationToken));
            measurements.Add(await MeasureParakeetAsync(fixtures, tenSecond, threads, biasing: false, cancellationToken));
        }

        // Biasing only at the default thread count: the question is what beam search costs
        // relative to greedy, and re-measuring that at every thread count would triple the run
        // for an axis that does not interact with it.
        measurements.Add(await MeasureParakeetAsync(fixtures, oneSecond, 4, biasing: true, cancellationToken));
        measurements.Add(await MeasureParakeetAsync(fixtures, tenSecond, 4, biasing: true, cancellationToken));

        foreach (var quant in ModelCatalog.WhisperQuants)
        {
            var path = quant.ResolvePath(environment.Paths.Models);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  [skip] {quant.Id} -- not downloaded ({quant.SizeDescription}).");
                continue;
            }

            measurements.Add(await MeasureWhisperAsync(fixtures, oneSecond, quant, path, cancellationToken));
            measurements.Add(await MeasureWhisperAsync(fixtures, tenSecond, quant, path, cancellationToken));
        }

        var (selection, gates) = Choose(measurements);
        var report = new BenchReport(
            DateTimeOffset.Now,
            $"{Environment.ProcessorCount}-thread CPU, {Environment.OSVersion.VersionString}",
            measurements,
            selection,
            gates);

        Print(report);

        var path2 = Path.Combine(environment.RepoRoot, "bench-report.json");
        await report.WriteAsync(path2, cancellationToken);
        await PersistSelectionAsync(selection, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"  Report written to {path2}");
        Console.WriteLine($"  Selection persisted to {environment.Paths.SettingsFile}");

        // A failed gate is reported, not thrown: the selection is still the best available and
        // the app still works. `eval` is the command that gates CI.
        return 0;
    }

    private async Task<IReadOnlyList<LoadedFixture>> EnsureFixturesAsync(CancellationToken cancellationToken)
    {
        var builder = new FixtureBuilder(environment.RepoRoot);
        var entries = await builder.BuildAsync(force: false, cancellationToken);

        var loaded = new List<LoadedFixture>();
        foreach (var entry in entries)
        {
            var path = FixtureCorpus.ResolveAudio(environment.RepoRoot, entry);
            if (!File.Exists(path))
            {
                continue;
            }

            loaded.Add(new LoadedFixture(entry, WaveFile.Read(path)));
        }

        return loaded;
    }

    /// <summary>
    /// Builds a single utterance of about the requested length by concatenating fixtures.
    /// </summary>
    /// <remarks>
    /// Concatenating rather than padding with silence: a 10 s buffer that is 3 s of speech and 7 s
    /// of quiet measures the VAD, not the recogniser, and would make every engine look fast.
    /// </remarks>
    private static float[] BuildUtterance(IReadOnlyList<LoadedFixture> fixtures, double targetSeconds)
    {
        var target = AudioFormat.SamplesFor(TimeSpan.FromSeconds(targetSeconds));
        var buffer = new List<float>(target);

        foreach (var fixture in fixtures.OrderBy(f => f.Entry.Id, StringComparer.Ordinal))
        {
            buffer.AddRange(fixture.Samples);
            if (buffer.Count >= target)
            {
                break;
            }
        }

        if (buffer.Count == 0)
        {
            return new float[target];
        }

        // Trim rather than pad, so the measured length is at most the target and the RTF is not
        // flattered by trailing silence.
        return buffer.Count > target ? buffer.GetRange(0, target).ToArray() : buffer.ToArray();
    }

    private async Task<BenchMeasurement> MeasureParakeetAsync(
        IReadOnlyList<LoadedFixture> fixtures,
        float[] utterance,
        int threads,
        bool biasing,
        CancellationToken cancellationToken)
    {
        var directory = ModelCatalog.ParakeetV2Int8.ResolvePath(environment.Paths.Models);
        var options = new ParakeetOptions(directory, threads, EnableHotwordBiasing: biasing);

        return await MeasureAsync(
            () => new ParakeetRecognizer(options),
            fixtures, utterance, threads,
            biasing ? "modified_beam_search" : "greedy_search",
            biasing,
            cancellationToken);
    }

    private async Task<BenchMeasurement> MeasureWhisperAsync(
        IReadOnlyList<LoadedFixture> fixtures,
        float[] utterance,
        ModelAsset quant,
        string path,
        CancellationToken cancellationToken)
    {
        var options = new WhisperOptions(path, quant.Id, NumThreads: 4);
        return await MeasureAsync(
            () => new WhisperNetRecognizer(options),
            fixtures, utterance, 4, "greedy", useHotwords: false, cancellationToken);
    }

    private static async Task<BenchMeasurement> MeasureAsync(
        Func<ISpeechRecognizer> create,
        IReadOnlyList<LoadedFixture> fixtures,
        float[] utterance,
        int threads,
        string decodingMode,
        bool useHotwords,
        CancellationToken cancellationToken)
    {
        var seconds = utterance.Length / (double)AudioFormat.SampleRate;

        // Cold pass: a brand-new engine instance, measured separately from everything after it.
        // This is the number that decides whether the model can be loaded on demand at all.
        double coldInit, firstInference;
        string engineId;
        {
            using var cold = create();
            var initStopwatch = Stopwatch.StartNew();
            await cold.LoadAsync(cancellationToken);
            initStopwatch.Stop();
            coldInit = initStopwatch.Elapsed.TotalMilliseconds;
            engineId = cold.EngineId;

            var firstStopwatch = Stopwatch.StartNew();
            await cold.TranscribeAsync(utterance, RecognitionOptions.Default, cancellationToken);
            firstStopwatch.Stop();
            firstInference = firstStopwatch.Elapsed.TotalMilliseconds;
        }

        using var recognizer = create();
        await recognizer.LoadAsync(cancellationToken);

        var hotwords = useHotwords
            ? fixtures.SelectMany(f => f.Entry.Hotwords ?? []).Distinct().ToArray()
            : [];
        var options = new RecognitionOptions(hotwords.Length > 0 ? hotwords : null);

        // One discarded run: the first call through a freshly loaded engine still pays JIT and
        // first-touch page faults, and including it would report a warm p50 that is not warm.
        await recognizer.TranscribeAsync(utterance, options, cancellationToken);

        var samples = new List<double>(WarmRuns);
        for (var i = 0; i < WarmRuns; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            await recognizer.TranscribeAsync(utterance, options, cancellationToken);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var warm = LatencyStats.From(samples);

        // Accuracy is measured per fixture against its own reference -- the concatenated utterance
        // has no meaningful ground truth, and scoring against a joined-up reference would reward
        // an engine for guessing sentence boundaries.
        var errors = new List<double>();
        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await recognizer.TranscribeAsync(fixture.Samples, options, cancellationToken);
            errors.Add(WordErrorRate.Rate(fixture.Entry.Reference, result.Text));
        }

        return new BenchMeasurement(
            EngineId: engineId,
            DecodingMode: decodingMode,
            UtteranceSeconds: Math.Round(seconds, 2),
            NumThreads: threads,
            ColdInitMs: Math.Round(coldInit, 1),
            FirstInferenceMs: Math.Round(firstInference, 1),
            ColdPathMs: Math.Round(coldInit + firstInference, 1),
            Warm: warm,
            RealTimeFactor: Math.Round(warm.P50Ms / 1000 / seconds, 4),
            WordErrorRate: Math.Round(errors.Count == 0 ? 1 : errors.Average(), 4),
            FixtureCount: fixtures.Count);
    }

    /// <summary>
    /// Picks the configuration to ship, on measurement alone.
    /// </summary>
    /// <remarks>
    /// Accuracy first, then latency: the user said "It needs to be accurate. It needs to be
    /// relatively fast, but again, I don't mind some lag." Engines within a small WER band of the
    /// best are treated as equally accurate -- the corpus is not large enough for a fraction of a
    /// point to be real -- and the fastest of those wins.
    /// </remarks>
    private static (BenchSelection, IReadOnlyList<BenchGate>) Choose(IReadOnlyList<BenchMeasurement> measurements)
    {
        const double WerTieBand = 0.02;

        var greedy = measurements.Where(m => !m.DecodingMode.Contains("beam", StringComparison.Ordinal)).ToArray();
        var shortRuns = greedy.Where(m => m.UtteranceSeconds <= 2).ToArray();
        var pool = shortRuns.Length > 0 ? shortRuns : greedy;

        var bestWer = pool.Min(m => m.WordErrorRate);
        var contenders = pool.Where(m => m.WordErrorRate <= bestWer + WerTieBand).ToArray();
        var winner = contenders.MinBy(m => m.Warm.P50Ms)!;

        // Biasing is enabled only if beam search stayed inside its budget relative to greedy at
        // the same shape. Phases 8 and 10 read this decision rather than assuming it.
        var biasedShort = measurements.FirstOrDefault(m =>
            m.DecodingMode.Contains("beam", StringComparison.Ordinal) && m.UtteranceSeconds <= 2);
        var greedyShort = measurements.FirstOrDefault(m =>
            m.EngineId == winner.EngineId && !m.DecodingMode.Contains("beam", StringComparison.Ordinal) &&
            m.UtteranceSeconds <= 2 && m.NumThreads == 4);

        var biasingRatio = biasedShort is not null && greedyShort is { Warm.P50Ms: > 0 }
            ? biasedShort.Warm.P50Ms / greedyShort.Warm.P50Ms
            : double.PositiveInfinity;
        var biasingOk = biasingRatio <= BiasingOverheadBudgetRatio;

        var gates = new List<BenchGate>
        {
            new("cold_path_1s", ColdPathBudgetMs, winner.ColdPathMs, winner.ColdPathMs <= ColdPathBudgetMs,
                "Cold session-init plus the first 1 s transcription. Jane preloads the engine at startup, so this is a startup cost rather than a per-dictation one -- but it is the number that proves residency is required."),
            new("warm_short", WarmShortBudgetMs, winner.Warm.P50Ms, winner.Warm.P50Ms <= WarmShortBudgetMs,
                "Warm p50 for a short utterance -- what the user actually waits for on a resident engine."),
            new("hotword_biasing_overhead", BiasingOverheadBudgetRatio * 100,
                double.IsInfinity(biasingRatio) ? -1 : Math.Round(biasingRatio * 100, 1), biasingOk,
                biasingOk
                    ? "Beam search stayed inside budget, so contextual biasing is enabled for Deep Context and the dictionary."
                    : "Beam search cost too much relative to greedy, so biasing is disabled. Phases 8 and 10 fall back to prompt-side hints only."),
        };

        var reason = string.Create(CultureInfo.InvariantCulture,
            $"{winner.EngineId} at {winner.NumThreads} threads: WER {winner.WordErrorRate:P1} (best {bestWer:P1}), warm p50 {winner.Warm.P50Ms:F0} ms, cold path {winner.ColdPathMs:F0} ms. " +
            $"Chosen as the fastest configuration within {WerTieBand:P0} WER of the best.");

        return (new BenchSelection(winner.EngineId, winner.NumThreads, biasingOk, reason), gates);
    }

    private async Task PersistSelectionAsync(BenchSelection selection, CancellationToken cancellationToken)
    {
        using var store = new SettingsStore(environment.Paths);
        await store.UpdateAsync(settings => settings with
        {
            Speech = settings.Speech with
            {
                EngineId = selection.EngineId,
                NumThreads = selection.NumThreads,
                EnableHotwordBiasing = selection.HotwordBiasingEnabled,
                WhisperModelId = selection.EngineId.StartsWith("whisper", StringComparison.Ordinal)
                    ? selection.EngineId
                    : null,
            },
            BenchmarkedAt = DateTimeOffset.Now,
        }, cancellationToken);
    }

    private static void Print(BenchReport report)
    {
        Console.WriteLine($"  {"engine",-32} {"decode",-20} {"utt",6} {"thr",4} {"cold",8} {"first",8} {"p50",8} {"p95",8} {"rtf",7} {"wer",7}");
        foreach (var m in report.Measurements)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {m.EngineId,-32} {m.DecodingMode,-20} {m.UtteranceSeconds,6:F1} {m.NumThreads,4} " +
                $"{m.ColdInitMs,8:F0} {m.FirstInferenceMs,8:F0} {m.Warm.P50Ms,8:F0} {m.Warm.P95Ms,8:F0} " +
                $"{m.RealTimeFactor,7:F3} {m.WordErrorRate,7:P1}"));
        }

        Console.WriteLine();
        foreach (var gate in report.Gates)
        {
            Console.WriteLine($"  [{(gate.Passed ? "PASS" : "MISS")}] {gate.Name,-26} measured {gate.MeasuredMs:F0} vs budget {gate.BudgetMs:F0}");
            Console.WriteLine($"           {gate.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Selected: {report.Selection.Reason}");
        Console.WriteLine($"  Hotword biasing: {(report.Selection.HotwordBiasingEnabled ? "enabled" : "disabled")}");
    }

    private sealed record LoadedFixture(FixtureEntry Entry, float[] Samples);
}
