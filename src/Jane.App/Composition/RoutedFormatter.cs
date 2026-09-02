using Jane.Core.Abstractions;

namespace Jane.App.Composition;

/// <summary>
/// Applies the governor's routing decision to formatting: on the Skip route the transcript is
/// returned untouched, and the LLM is never contacted at all.
/// </summary>
/// <remarks>
/// This is where the locked in-game behaviour actually happens. Skipping means skipping: no
/// warm-up, no request, no model load, nothing on the GPU and nothing on the CPU. Raw Parakeet
/// output already carries punctuation and casing, so what the user loses is filler removal and
/// self-correction resolution, not readability.
/// <para>
/// A decorator rather than a branch inside the formatter, so the formatter stays a pure
/// transcript-to-text function and the resource policy stays in one place.
/// </para>
/// </remarks>
public sealed class RoutedFormatter(
    ITranscriptFormatter inner,
    Func<LlmRoute> route,
    Action<string>? onSkipped = null) : ITranscriptFormatter
{
    public async Task<string> FormatAsync(
        string transcript, FormattingContext context, CancellationToken cancellationToken)
    {
        var current = route();

        if (current == LlmRoute.Skip)
        {
            onSkipped?.Invoke(transcript);
            return transcript;
        }

        try
        {
            return await inner.FormatAsync(transcript, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A formatting failure must never cost the user their dictation. The raw transcript is
            // already presentable text; losing it because a model misbehaved would be the worse
            // outcome by far.
            onSkipped?.Invoke(transcript);
            return transcript;
        }
    }
}
