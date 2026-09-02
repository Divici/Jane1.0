using System.Threading.Tasks;
using Jane.Core.Abstractions;
using Jane.Core.Formatting;
using Jane.Core.History;

namespace Jane.App.Composition;

/// <summary>
/// Writes every formatting outcome into history, including the ones where the LLM never ran.
/// </summary>
/// <remarks>
/// The bypasses are the point. Phase 12 measures the *false-bypass rate* -- how often Jane decided
/// a transcript needed no cleaning and was wrong -- and that number does not exist unless the
/// skipped dictations are recorded with the reason they were skipped. A log that only captured
/// successful LLM calls would make the bypass heuristic unfalsifiable.
/// <para>
/// Recording is fire-and-forget on purpose. History is a diary, not a dependency: a database that
/// is slow, locked or full must never be able to hold up text the user is waiting for.
/// </para>
/// </remarks>
public sealed class HistoryFormattingLog(HistoryStore history, Func<TargetWindow> target) : IFormattingLog
{
    public void Record(FormattingOutcome outcome)
    {
        var window = target();

        var entry = new HistoryEntry
        {
            CreatedAt = outcome.At,
            Mode = DictationMode.Dictation,
            RawTranscript = outcome.RawTranscript,
            FinalText = outcome.FinalText,
            Target = window,
            Bypassed = outcome.Route == FormattingRoute.Bypassed,

            // The prose reason, plus the structured blocker list flattened into it. HistoryEntry
            // has no column for the blockers, and losing "which conditions failed" would leave
            // Phase 12 unable to tell "one condition blocked it" from "all four did".
            BypassReason = DescribeBypass(outcome),
            LlmModel = outcome.Route == FormattingRoute.Formatted ? outcome.Model : null,
            Timings = StageTimings.Empty with { Formatting = outcome.Elapsed, Total = outcome.Elapsed },
        };

        _ = RecordAsync(entry);
    }

    private async Task RecordAsync(HistoryEntry entry)
    {
        try
        {
            await history.AppendAsync(entry, CancellationToken.None);
        }
        catch (Exception)
        {
            // Deliberately swallowed. A failed history write must not surface as a failed
            // dictation: the user already has their text.
        }
    }

    private static string DescribeBypass(FormattingOutcome outcome)
    {
        var blockers = outcome.Bypass.Blockers.Count == 0
            ? string.Empty
            : $" [blockers: {string.Join(", ", outcome.Bypass.Blockers)}]";

        var fallback = outcome.Route == FormattingRoute.RawFallback
            ? $" [fell back to raw: {outcome.Fallback}]"
            : string.Empty;

        return outcome.Bypass.Reason + blockers + fallback;
    }
}

/// <summary>
/// Records the dictations the governor skipped before the formatter ever saw them.
/// </summary>
/// <remarks>
/// Without this, an in-game dictation produces no history row at all: <see cref="RoutedFormatter"/>
/// returns the raw transcript without invoking the formatter, so no <see cref="FormattingOutcome"/>
/// is ever created. Phase 12's in-game route numbers would have nothing to measure.
/// </remarks>
public sealed class SkippedDictationLog(HistoryStore history, Func<TargetWindow> target)
{
    public void Record(string transcript)
    {
        var window = target();

        _ = RecordAsync(new HistoryEntry
        {
            CreatedAt = DateTimeOffset.Now,
            Mode = DictationMode.Dictation,
            RawTranscript = transcript,
            FinalText = transcript,
            Target = window,
            Bypassed = true,
            BypassReason = "The GPU governor routed this dictation to LLM-off, so the raw transcript was injected unchanged.",
        });
    }

    private async Task RecordAsync(HistoryEntry entry)
    {
        try
        {
            await history.AppendAsync(entry, CancellationToken.None);
        }
        catch (Exception)
        {
            // See HistoryFormattingLog: history must never fail a dictation.
        }
    }
}
