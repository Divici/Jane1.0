namespace Jane.Core.Abstractions;

/// <param name="ContainsSpeech">
/// False means the buffer held no speech at all. The pipeline stops here without ever waking the
/// recogniser, which is what makes a stray press of a common game bind free rather than merely
/// quiet.
/// </param>
/// <param name="Trimmed">Leading and trailing silence removed. Empty when there was no speech.</param>
public readonly record struct VoiceActivityResult(bool ContainsSpeech, ReadOnlyMemory<float> Trimmed);

/// <summary>Trims silence from a finished utterance and says whether anything was spoken.</summary>
/// <remarks>
/// Separate from <see cref="ISpeechRecognizer"/> so the orchestrator can be tested without the
/// VAD model, and so a missing VAD model can be a typed failure rather than a silent
/// pass-through: dictation that still "works" while quietly feeding untrimmed audio to the
/// recogniser reads as a model-quality problem for weeks (`OpenWhispr#1057`).
/// </remarks>
public interface IVoiceActivityGate
{
    VoiceActivityResult Process(ReadOnlyMemory<float> samples);
}

/// <param name="TargetProcessName">Drives per-app Custom Instructions from Phase 10.</param>
/// <param name="Hotwords">Dictionary terms and Deep Context extractions, for the prompt side.</param>
public sealed record FormattingContext(
    string TargetProcessName,
    IReadOnlyList<string>? Hotwords = null,
    string? ScreenContext = null)
{
    public static FormattingContext Empty { get; } = new(string.Empty);
}

/// <summary>
/// Turns a raw transcript into what the user meant to write.
/// </summary>
/// <remarks>
/// Phase 5 wires a pass-through implementation so the end-to-end path works with no LLM at all;
/// Phase 7 replaces it. Keeping the seam from the start is what lets the in-game route (LLM
/// skipped entirely) and the bypass route be the same code path as the full one.
/// </remarks>
public interface ITranscriptFormatter
{
    Task<string> FormatAsync(string transcript, FormattingContext context, CancellationToken cancellationToken);
}

/// <summary>The recognition engine could not be brought up at all.</summary>
public sealed class SpeechEngineUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
