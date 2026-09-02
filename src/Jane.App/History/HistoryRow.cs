using System.Globalization;
using Jane.App.Controls;
using Jane.Core.History;

namespace Jane.App.History;

/// <summary>
/// One dictation, as the history window shows it: <em>when</em> and <em>where</em> beside
/// <em>what</em>.
/// </summary>
/// <remarks>
/// <para>
/// The window is a log, and a log of text alone is unusable -- "did I dictate that into the pull
/// request or into Slack" is the question people actually bring to it. So the application and the
/// window title are on every row, at the same weight as the timestamp, and the text is what the
/// row is sized around.
/// </para>
/// <para>
/// The raw transcript is kept separately and shown only when it differs from the final text.
/// Showing both always would double the height of every row to display the same sentence twice;
/// showing it when it differs is the only time it says anything -- it is the difference the
/// language model made.
/// </para>
/// </remarks>
public sealed class HistoryRow(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;

    public long Id => Entry.Id;

    /// <summary>What was actually typed.</summary>
    public string Text => Entry.FinalText;

    public string RawTranscript => Entry.RawTranscript;

    /// <summary>Whether the language model changed anything. Only then is the raw text worth showing.</summary>
    public bool ShowsRawTranscript =>
        !string.Equals(Entry.RawTranscript.Trim(), Entry.FinalText.Trim(), StringComparison.Ordinal);

    /// <summary>Process name as the focus tracker recorded it, or a plain "unknown".</summary>
    public string Application =>
        string.IsNullOrWhiteSpace(Entry.Target.ProcessName) ? "unknown" : Entry.Target.ProcessName;

    public string WindowTitle =>
        string.IsNullOrWhiteSpace(Entry.Target.WindowTitle) ? "(no window title recorded)" : Entry.Target.WindowTitle;

    /// <summary>Clock time, local. The date is only shown when it is not today.</summary>
    public string When
    {
        get
        {
            var local = Entry.CreatedAt.ToLocalTime();

            return local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
                : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The full instant, for the tooltip and the accessible name.</summary>
    public string WhenDetail =>
        Entry.CreatedAt.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>"4 minutes ago". What a person actually reaches for when scanning a log.</summary>
    public string Relative
    {
        get
        {
            var elapsed = DateTimeOffset.Now - Entry.CreatedAt;

            return elapsed switch
            {
                { TotalSeconds: < 60 } => "just now",
                { TotalMinutes: < 60 } => Plural((int)elapsed.TotalMinutes, "minute"),
                { TotalHours: < 24 } => Plural((int)elapsed.TotalHours, "hour"),
                { TotalDays: < 30 } => Plural((int)elapsed.TotalDays, "day"),
                _ => Plural((int)(elapsed.TotalDays / 30), "month"),
            };
        }
    }

    public string ModeLabel => Entry.Mode == DictationMode.Edit ? "Edit Mode" : "Dictation";

    /// <summary>
    /// Whether the language model ran, and if not, why not.
    /// </summary>
    /// <remarks>
    /// The reason is recorded on every row rather than only the bypassed ones, because Phase 12
    /// measures the false-bypass rate from this column and a bypass nobody can audit is worse
    /// than no bypass at all.
    /// </remarks>
    public string FormattingLabel => Entry switch
    {
        { Bypassed: true, BypassReason.Length: > 0 } => "Raw: " + Entry.BypassReason,
        { Bypassed: true } => "Raw transcript, no cleanup",
        { LlmModel: { Length: > 0 } model } => "Formatted by " + model,
        _ => "Raw transcript",
    };

    public Severity FormattingSeverity => Entry.Bypassed ? Severity.Warning : Severity.Info;

    public bool HasDeepContext => !string.IsNullOrWhiteSpace(Entry.DeepContext);

    /// <summary>The on-screen text Deep Context read, in plaintext. The window says so.</summary>
    public string? DeepContext => Entry.DeepContext;

    public bool WasInjected => Entry.Injected;

    public string InjectionLabel => Entry switch
    {
        { Injected: true } => "Typed",
        { InjectionFailure: { Length: > 0 } failure } => "Not typed: " + failure,
        _ => "Not typed",
    };

    public Severity InjectionSeverity => Entry.Injected ? Severity.Success : Severity.Warning;

    /// <summary>Where the time went, in one line. Zero stages are left out rather than shown as 0 ms.</summary>
    public string TimingSummary
    {
        get
        {
            var parts = new List<string>();

            Add(parts, "speech", Entry.Timings.Recognition);
            Add(parts, "context", Entry.Timings.Context);
            Add(parts, "cleanup", Entry.Timings.Formatting);
            Add(parts, "typing", Entry.Timings.Injection);

            var total = Entry.Timings.Total.TotalMilliseconds;
            var head = total >= 1000
                ? string.Create(CultureInfo.CurrentCulture, $"{total / 1000:0.0} s total")
                : string.Create(CultureInfo.CurrentCulture, $"{total:0} ms total");

            return parts.Count == 0 ? head : head + "  -  " + string.Join(", ", parts);
        }
    }

    public string EngineLabel => Entry.EngineId;

    /// <summary>What a screen reader reads for the whole row, in the order a person would want it.</summary>
    public string Announcement =>
        $"{WhenDetail}, in {Application}, {WindowTitle}. {Text}";

    private static void Add(List<string> parts, string name, TimeSpan span)
    {
        if (span > TimeSpan.Zero)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{name} {span.TotalMilliseconds:0} ms"));
        }
    }

    private static string Plural(int count, string unit) =>
        count <= 1 ? $"1 {unit} ago" : string.Create(CultureInfo.CurrentCulture, $"{count} {unit}s ago");
}
