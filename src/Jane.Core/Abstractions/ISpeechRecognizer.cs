namespace Jane.Core.Abstractions;

/// <param name="Hotwords">
/// Terms to bias recognition toward -- dictionary entries and Deep Context extractions.
/// Parakeet maps these to sherpa-onnx contextual biasing, which forces
/// <c>modified_beam_search</c>; Whisper.net maps them into the initial-prompt text. Because the
/// decoder change can cost real latency, biasing is only enabled when `bench` measured it
/// staying inside budget.
/// </param>
/// <param name="HotwordBoost">Bias strength. sherpa-onnx's scale; ignored by Whisper.</param>
public sealed record RecognitionOptions(
    IReadOnlyList<string>? Hotwords = null,
    float HotwordBoost = 1.5f,
    bool EnableTimestamps = true)
{
    public static RecognitionOptions Default { get; } = new();

    public bool HasHotwords => Hotwords is { Count: > 0 };
}

/// <param name="Start">Seconds from the start of the supplied buffer.</param>
public sealed record WordTiming(string Word, double Start, double End);

/// <param name="Load">
/// Cost of bringing the engine up. Reported separately because the cold path is the number that
/// decides whether the model stays RAM-resident, and a warm-only average would hide it.
/// </param>
public sealed record RecognitionTimings(TimeSpan Load, TimeSpan Feature, TimeSpan Decode, TimeSpan Total);

/// <param name="Text">Punctuated, cased text. Parakeet emits both natively; no restoration pass.</param>
/// <param name="DecodingMethod">Which decoder actually ran -- greedy or beam. The bench reports on this axis.</param>
public sealed record RecognitionResult(
    string Text,
    IReadOnlyList<WordTiming> Words,
    RecognitionTimings Timings,
    string DecodingMethod)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// A speech recogniser over a finished utterance.
/// </summary>
/// <remarks>
/// Batch, not streaming: the user chose accuracy over live word-by-word display, and streaming
/// WER is 1-3 points worse.
///
/// Implementations are expected to stay loaded for the app's lifetime. Parakeet int8 costs about
/// 2 GB of RAM and zero VRAM, and RAM is not the constrained resource here -- whereas a
/// load-on-demand engine would make the cold path the everyday path.
/// </remarks>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Stable identifier used in bench reports and settings, e.g. <c>parakeet-tdt-0.6b-v2-int8</c>.</summary>
    string EngineId { get; }

    bool IsLoaded { get; }

    /// <summary>Brings the model into memory. Safe to call more than once.</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <param name="pcm16k">16 kHz mono float samples in [-1, 1].</param>
    Task<RecognitionResult> TranscribeAsync(
        ReadOnlyMemory<float> pcm16k,
        RecognitionOptions options,
        CancellationToken cancellationToken);
}
