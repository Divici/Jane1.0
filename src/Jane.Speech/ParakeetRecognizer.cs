using System.Diagnostics;
using Jane.Core.Abstractions;
using SherpaOnnx;

namespace Jane.Speech;

/// <param name="NumThreads">
/// Parakeet's published figures show 0.220 RTF single-threaded and 0.088 on four threads, so
/// threads past four buy little and cost more of a running game's CPU. Four is the default on
/// this 8-core part; the governor drops the whole inference to BelowNormal while a game is up.
/// </param>
/// <param name="EnableHotwordBiasing">
/// Off unless `bench` proved it stays in budget. Contextual biasing forces
/// <c>modified_beam_search</c> in place of greedy decoding, which can cost real latency -- the
/// plan makes this an explicit bench axis rather than an assumption.
/// </param>
public sealed record ParakeetOptions(
    string ModelDirectory,
    int NumThreads = 4,
    bool EnableHotwordBiasing = false,
    float HotwordScore = 1.5f,
    int MaxActivePaths = 4);

public sealed class SpeechModelMissingException(string message) : Exception(message)
{
    /// <summary>Shown verbatim in onboarding and the error toast.</summary>
    public string Remedy =>
        "Download the speech model from Jane's settings, or run `dotnet run --project src/Jane.Bench -- doctor` to check the model directory.";
}

/// <summary>
/// NVIDIA Parakeet-TDT-0.6B-v2, int8, on the CPU via sherpa-onnx.
/// </summary>
/// <remarks>
/// CPU is a deliberate choice, not a fallback. Every GPU path on this machine is compromised:
/// whisper.cpp's Vulkan backend has an open kernel-level BSOD on RTX 50-series running this exact
/// Windows build, its prebuilt CUDA binaries target 11.8/12.4 and not sm_120, and onnxruntime-gpu
/// ships no sm_120 kernels and falls back to CPU *silently* -- which would look like working
/// acceleration while being 10-50x slower. The CPU provider sidesteps all of it, holds zero VRAM,
/// and leaves the GPU free, which is the user's hard constraint.
///
/// The recogniser stays loaded for the app's lifetime. It costs about 2 GB of RAM of 31 GB and no
/// VRAM at all, and residency is what stops the cold path from being the everyday path.
/// </remarks>
public sealed class ParakeetRecognizer(ParakeetOptions options) : ISpeechRecognizer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private string? _hotwordsFile;
    private string _appliedHotwords = string.Empty;
    private TimeSpan _loadDuration;
    private bool _disposed;

    public string EngineId => "parakeet-tdt-0.6b-v2-int8";

    public bool IsLoaded => _recognizer is not null;

    /// <summary>Cold session-init cost, kept so `bench` can report it without re-measuring.</summary>
    public TimeSpan LoadDuration => _loadDuration;

    public ParakeetOptions Options => options;

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recognizer is not null)
        {
            return Task.CompletedTask;
        }

        var missing = ModelCatalog.ParakeetComponents
            .Where(c => !File.Exists(Path.Combine(options.ModelDirectory, c)))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new SpeechModelMissingException(
                $"Parakeet model incomplete in {options.ModelDirectory}: missing {string.Join(", ", missing)}.");
        }

        var stopwatch = Stopwatch.StartNew();
        _recognizer = new OfflineRecognizer(BuildConfig(hotwordsFile: null));
        stopwatch.Stop();
        _loadDuration = stopwatch.Elapsed;

        return Task.CompletedTask;
    }

    private OfflineRecognizerConfig BuildConfig(string? hotwordsFile)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(options.ModelDirectory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(options.ModelDirectory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(options.ModelDirectory, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(options.ModelDirectory, "tokens.txt");
        config.ModelConfig.NumThreads = options.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Debug = 0;

        // Biasing only works under modified_beam_search; greedy ignores the hotword list entirely.
        // That coupling is exactly why the bench measures the two decoders separately.
        var biasing = options.EnableHotwordBiasing && !string.IsNullOrEmpty(hotwordsFile);
        config.DecodingMethod = biasing ? "modified_beam_search" : "greedy_search";
        config.MaxActivePaths = options.MaxActivePaths;
        config.HotwordsScore = options.HotwordScore;
        config.HotwordsFile = biasing ? hotwordsFile! : string.Empty;

        // The feature sample rate has to agree with what the capture produces, or the encoder
        // silently sees the wrong time base and the transcript degrades without an error.
        config.FeatConfig.SampleRate = AudioFormat.SampleRate;
        config.FeatConfig.FeatureDim = 80;

        return config;
    }

    public async Task<RecognitionResult> TranscribeAsync(
        ReadOnlyMemory<float> pcm16k,
        RecognitionOptions recognitionOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var loadStopwatch = Stopwatch.StartNew();
        if (_recognizer is null)
        {
            await LoadAsync(cancellationToken);
        }

        loadStopwatch.Stop();

        // sherpa-onnx's managed surface is synchronous and CPU-bound; running it inline would
        // block whichever thread the orchestrator is on, including the UI dispatcher.
        return await Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decodingMethod = ApplyHotwords(recognitionOptions);
                var recognizer = _recognizer!;

                var total = Stopwatch.StartNew();
                var featureStopwatch = Stopwatch.StartNew();
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(AudioFormat.SampleRate, pcm16k.ToArray());
                featureStopwatch.Stop();

                var decodeStopwatch = Stopwatch.StartNew();
                recognizer.Decode(stream);
                decodeStopwatch.Stop();
                total.Stop();

                var result = stream.Result;

                return new RecognitionResult(
                    Text: result.Text.Trim(),
                    Words: BuildWordTimings(result, recognitionOptions.EnableTimestamps),
                    Timings: new RecognitionTimings(
                        Load: loadStopwatch.Elapsed,
                        Feature: featureStopwatch.Elapsed,
                        Decode: decodeStopwatch.Elapsed,
                        Total: total.Elapsed),
                    DecodingMethod: decodingMethod);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Pushes the request's hotwords into the live recogniser, and returns the decoder that will
    /// actually run.
    /// </summary>
    /// <remarks>
    /// sherpa-onnx takes hotwords as a file path on the config, not as a per-stream argument, so
    /// a changed vocabulary means writing the file and calling <c>SetConfig</c>. That is cheap
    /// relative to reconstructing the recogniser (which would re-pay the multi-second session
    /// init), but it is not free, so it only happens when the set actually changed.
    /// </remarks>
    private string ApplyHotwords(RecognitionOptions recognitionOptions)
    {
        if (!options.EnableHotwordBiasing)
        {
            return "greedy_search";
        }

        var wanted = recognitionOptions.HasHotwords
            ? string.Join('\n', recognitionOptions.Hotwords!.Select(Normalise).Where(w => w.Length > 0).Distinct())
            : string.Empty;

        if (wanted == _appliedHotwords)
        {
            return wanted.Length == 0 ? "greedy_search" : "modified_beam_search";
        }

        _appliedHotwords = wanted;

        if (wanted.Length == 0)
        {
            _recognizer!.SetConfig(BuildConfig(hotwordsFile: null));
            return "greedy_search";
        }

        _hotwordsFile ??= Path.Combine(Path.GetTempPath(), $"jane-hotwords-{Environment.ProcessId}.txt");
        File.WriteAllText(_hotwordsFile, wanted + '\n');
        _recognizer!.SetConfig(BuildConfig(_hotwordsFile));
        return "modified_beam_search";
    }

    /// <summary>
    /// sherpa-onnx matches hotwords against the model's token vocabulary, which for this
    /// checkpoint is upper-case with spaces between words. A lower-case entry silently never
    /// matches, which looks like biasing quietly not working.
    /// </summary>
    private static string Normalise(string word) => word.Trim().ToUpperInvariant();

    private static IReadOnlyList<WordTiming> BuildWordTimings(OfflineRecognizerResult result, bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        var tokens = result.Tokens;
        var stamps = result.Timestamps;
        if (tokens is null || stamps is null || tokens.Length == 0)
        {
            return [];
        }

        // Parakeet emits sub-word tokens; a leading space (or the SentencePiece marker) opens a
        // word. Joining them here is what turns per-token stamps into the per-word timings the
        // dictionary and the eval corpus actually use.
        var words = new List<WordTiming>();
        var current = string.Empty;
        var start = 0d;
        var end = 0d;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var stamp = i < stamps.Length ? stamps[i] : end;
            var startsWord = token.StartsWith(' ') || token.StartsWith('▁');

            if (startsWord && current.Trim().Length > 0)
            {
                words.Add(new WordTiming(current.Trim(), start, stamp));
                current = string.Empty;
            }

            if (current.Length == 0)
            {
                start = stamp;
            }

            current += token.Replace('▁', ' ');
            end = stamp;
        }

        if (current.Trim().Length > 0)
        {
            words.Add(new WordTiming(current.Trim(), start, end));
        }

        return words;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer?.Dispose();
        _recognizer = null;
        _gate.Dispose();

        if (_hotwordsFile is not null && File.Exists(_hotwordsFile))
        {
            File.Delete(_hotwordsFile);
        }
    }
}
