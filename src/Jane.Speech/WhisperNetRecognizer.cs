using System.Diagnostics;
using Jane.Core.Abstractions;
using Whisper.net;

namespace Jane.Speech;

/// <param name="ModelPath">A whisper.cpp GGML file. The quantisation is part of the filename and part of the bench axis.</param>
/// <param name="EngineId">Carries the quant, e.g. <c>whisper-large-v3-turbo-q5_0</c>, so bench rows are distinguishable.</param>
public sealed record WhisperOptions(
    string ModelPath,
    string EngineId,
    int NumThreads = 4,
    string Language = "en");

/// <summary>
/// whisper.cpp <c>large-v3-turbo</c> via Whisper.net, on the CPU.
/// </summary>
/// <remarks>
/// The second engine, and the reason the plan does not simply trust Parakeet's published figure:
/// that figure is an extrapolation from a Cortex A76, and an unvalidated extrapolation is exactly
/// the kind of assumption a bench exists to kill. Both engines sit behind the same interface so
/// `bench` can choose on measurement rather than on the plan's prediction.
///
/// CPU only. The prebuilt CUDA assets target 11.8/12.4 and not sm_120, and the Vulkan backend has
/// an open kernel-level BSOD on RTX 50-series running this exact Windows build.
///
/// Hotwords map to Whisper's initial prompt, which is a genuinely weaker mechanism than
/// sherpa-onnx's contextual biasing -- it nudges the decoder's prior rather than reweighting the
/// lattice. The contract is the same; the strength is not, and the bench reports both.
/// </remarks>
public sealed class WhisperNetRecognizer(WhisperOptions options) : ISpeechRecognizer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private TimeSpan _loadDuration;
    private bool _disposed;

    public string EngineId => options.EngineId;

    public bool IsLoaded => _factory is not null;

    public TimeSpan LoadDuration => _loadDuration;

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_factory is not null)
        {
            return Task.CompletedTask;
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new SpeechModelMissingException(
                $"Whisper model not found at {options.ModelPath}.");
        }

        var stopwatch = Stopwatch.StartNew();
        _factory = WhisperFactory.FromPath(options.ModelPath);
        stopwatch.Stop();
        _loadDuration = stopwatch.Elapsed;

        return Task.CompletedTask;
    }

    public async Task<RecognitionResult> TranscribeAsync(
        ReadOnlyMemory<float> pcm16k,
        RecognitionOptions recognitionOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var loadStopwatch = Stopwatch.StartNew();
        if (_factory is null)
        {
            await LoadAsync(cancellationToken);
        }

        loadStopwatch.Stop();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var total = Stopwatch.StartNew();

            var builder = _factory!.CreateBuilder()
                .WithLanguage(options.Language)
                .WithThreads(options.NumThreads)
                .WithNoContext();

            if (recognitionOptions.HasHotwords)
            {
                // Whisper has no biasing surface; the initial prompt is the only lever, and it is
                // a soft one. Keep it short -- a long prompt eats context and can make the decoder
                // echo the prompt back as transcript.
                builder = builder.WithPrompt(string.Join(", ", recognitionOptions.Hotwords!.Take(48)));
            }

            await using var processor = builder.Build();

            var text = new System.Text.StringBuilder();
            var words = new List<WordTiming>();

            var decodeStopwatch = Stopwatch.StartNew();
            await foreach (var segment in processor.ProcessAsync(pcm16k.ToArray(), cancellationToken))
            {
                text.Append(segment.Text);

                if (recognitionOptions.EnableTimestamps)
                {
                    // Whisper.net's segments are phrase-level, not word-level. Splitting on
                    // whitespace and spreading the segment's span evenly is an approximation, and
                    // it is labelled as one: Parakeet's real per-token stamps are one of the
                    // reasons it is the primary engine.
                    var parts = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var span = (segment.End - segment.Start).TotalSeconds / parts.Length;
                        for (var i = 0; i < parts.Length; i++)
                        {
                            var start = segment.Start.TotalSeconds + (i * span);
                            words.Add(new WordTiming(parts[i].Trim(), start, start + span));
                        }
                    }
                }
            }

            decodeStopwatch.Stop();
            total.Stop();

            return new RecognitionResult(
                Text: text.ToString().Trim(),
                Words: words,
                Timings: new RecognitionTimings(
                    Load: loadStopwatch.Elapsed,
                    Feature: TimeSpan.Zero,
                    Decode: decodeStopwatch.Elapsed,
                    Total: total.Elapsed),
                DecodingMethod: recognitionOptions.HasHotwords ? "greedy+initial_prompt" : "greedy");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}
