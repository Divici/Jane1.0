using System.Text.Json.Serialization;
using Jane.Core.Abstractions;

namespace Jane.Core.Formatting;

/// <summary>How one dictation's final text was produced.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FormattingRoute>))]
public enum FormattingRoute
{
    /// <summary>The LLM was skipped because every bypass condition held. Raw text injected.</summary>
    Bypassed,

    /// <summary>The model ran and its output passed validation.</summary>
    Formatted,

    /// <summary>The model ran and its output was discarded. Raw text injected instead.</summary>
    RawFallback,
}

/// <summary>
/// The full record of one formatting decision.
/// </summary>
/// <remarks>
/// Written on every dictation that reaches the formatter. The in-game skip happens one layer out,
/// in Phase 6's <c>RoutedFormatter</c>, and is not recorded here. Phase 12 computes
/// the false-bypass rate directly from these: take the rows where
/// <see cref="Route"/> is <see cref="FormattingRoute.Bypassed"/>, run
/// <see cref="RawTranscript"/> through the formatter with the bypass suppressed, and count the
/// ones whose output differs from what was actually injected. Nothing else is needed, which is
/// why the raw transcript is here rather than only the final text.
/// </remarks>
/// <param name="Fallback">
/// <see cref="FormattingFallback.None"/> unless the model's output was rejected. This is the
/// other number Phase 12 reports: how often a local 4B model has to be overruled.
/// </param>
/// <param name="Elapsed">Wall clock for the whole formatting stage, bypasses included.</param>
public sealed record FormattingOutcome(
    DateTimeOffset At,
    string ProcessName,
    string RawTranscript,
    string FinalText,
    FormattingRoute Route,
    BypassDecision Bypass,
    FormattingFallback Fallback,
    string? Detail,
    string Model,
    LlmDevice Device,
    TimeSpan Elapsed,
    int PromptTokens,
    int CompletionTokens)
{
    /// <summary>True when the user's text came straight from the recogniser.</summary>
    public bool UsedRawText => Route is not FormattingRoute.Formatted;
}

/// <summary>Where a finished decision goes.</summary>
/// <remarks>
/// An interface rather than a direct write to history, because <c>Jane.Core.Formatting</c> should
/// not know that a database exists and because the eval harness collects these in memory.
/// </remarks>
public interface IFormattingLog
{
    void Record(FormattingOutcome outcome);
}

/// <summary>Discards outcomes. The default until Phase 11's history is wired in.</summary>
public sealed class NullFormattingLog : IFormattingLog
{
    public static NullFormattingLog Instance { get; } = new();

    public void Record(FormattingOutcome outcome)
    {
    }
}
