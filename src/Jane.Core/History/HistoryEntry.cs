using Jane.Core.Abstractions;

namespace Jane.Core.History;

/// <summary>Which of Jane's two modes produced an entry.</summary>
public enum DictationMode
{
    /// <summary>Text was dictated into whatever had focus.</summary>
    Dictation,

    /// <summary>A live selection was rewritten in place.</summary>
    Edit,
}

/// <summary>
/// Where the time went, stage by stage.
/// </summary>
/// <remarks>
/// Stored as whole milliseconds. History is read by a person and aggregated by the eval harness
/// into p50/p95, and neither wants ticks; sub-millisecond precision is measured in `bench`, not
/// reconstructed from here.
/// </remarks>
/// <param name="Context">Deep Context. Concurrent with speech, so it usually costs nothing.</param>
/// <param name="Formatting">Zero when the LLM was bypassed or skipped for a game.</param>
public sealed record StageTimings(
    TimeSpan Capture,
    TimeSpan Vad,
    TimeSpan Recognition,
    TimeSpan Context,
    TimeSpan Formatting,
    TimeSpan Injection,
    TimeSpan Total)
{
    public static StageTimings Empty { get; } = new(
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>Whether an entry can be re-injected where it came from.</summary>
/// <remarks>Each value maps to designed toast text; the main thread wires the actual injection.</remarks>
public enum ReinjectCheck
{
    /// <summary>The recorded window is still there, still owned by the same process. Go ahead.</summary>
    Ready,

    /// <summary>The entry never had a target -- an aborted injection, or an imported row.</summary>
    NoRecordedTarget,

    /// <summary>Nothing has focus, or the window has closed.</summary>
    WindowGone,

    /// <summary>
    /// Something else is focused now. Includes the recycled-handle case: same HWND, different
    /// owner, which is exactly why the process id and name are recorded alongside the handle.
    /// </summary>
    DifferentWindow,
}

/// <summary>
/// One dictation, kept forever in plaintext until the user deletes it.
/// </summary>
/// <remarks>
/// <para>
/// Everything about a dictation is here except the audio, which is never written anywhere: the
/// buffer is released on every path out of the pipeline, including the failing ones, and there is
/// no column, field or format in this type that could carry it.
/// </para>
/// <para>
/// The whole of <see cref="Target"/> is recorded, not just the window handle, because Windows
/// recycles handles. Re-inject checks the process identity too, so a stale entry aimed at a
/// long-closed editor cannot type into whatever inherited its HWND.
/// </para>
/// </remarks>
public sealed record HistoryEntry
{
    /// <summary>Zero until the row is written. <c>HistoryStore.AppendAsync</c> returns it filled in.</summary>
    public long Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DictationMode Mode { get; init; } = DictationMode.Dictation;

    /// <summary>Exactly what the recogniser emitted, before any cleanup.</summary>
    public required string RawTranscript { get; init; }

    /// <summary>Exactly what was injected. Equal to the raw transcript on a bypassed dictation.</summary>
    public required string FinalText { get; init; }

    /// <summary>The window that had focus at key-down, and the one re-inject aims at.</summary>
    public TargetWindow Target { get; init; } = TargetWindow.None;

    /// <summary>
    /// The on-screen text Deep Context read, in plaintext.
    /// </summary>
    /// <remarks>
    /// Null when Deep Context was off, blocklisted, or timed out. This is the most sensitive
    /// thing Jane stores, and it is stored plainly rather than quietly: the history window says
    /// so, and delete removes it with everything else.
    /// </remarks>
    public string? DeepContext { get; init; }

    /// <summary>True when the LLM was skipped for this dictation.</summary>
    public bool Bypassed { get; init; }

    /// <summary>
    /// Why the LLM did or did not run, in words.
    /// </summary>
    /// <remarks>
    /// Phase 12 measures the false-bypass rate from this column, so it is recorded on both
    /// outcomes -- a bypass with no reason is a decision nobody can audit.
    /// </remarks>
    public string BypassReason { get; init; } = string.Empty;

    /// <summary>Which recogniser produced the raw transcript, e.g. <c>parakeet-tdt-0.6b-v2-int8</c>.</summary>
    public string EngineId { get; init; } = string.Empty;

    /// <summary>Which model formatted it, or null when nothing did.</summary>
    public string? LlmModel { get; init; }

    public bool Injected { get; init; }

    /// <summary>The <c>InjectionFailure</c> name when the text did not land. Null on success.</summary>
    public string? InjectionFailure { get; init; }

    public StageTimings Timings { get; init; } = StageTimings.Empty;

    /// <summary>
    /// Whether this entry can be re-injected into the window currently in front.
    /// </summary>
    /// <param name="live">
    /// What the focus tracker reports right now, or <see cref="TargetWindow.None"/> if nothing
    /// has focus.
    /// </param>
    public ReinjectCheck CheckReinject(TargetWindow live)
    {
        if (Target.IsNone)
        {
            return ReinjectCheck.NoRecordedTarget;
        }

        if (live.IsNone)
        {
            return ReinjectCheck.WindowGone;
        }

        return Target.MatchesIdentity(live) ? ReinjectCheck.Ready : ReinjectCheck.DifferentWindow;
    }
}

/// <summary>
/// What to look for in history.
/// </summary>
/// <remarks>
/// Every filter is optional and they combine with AND. The default is "the most recent 200 rows",
/// which is what the history window opens on.
/// </remarks>
public sealed record HistoryQuery
{
    /// <summary>
    /// Substring, case-insensitive, matched against the raw transcript and the final text.
    /// Wildcard characters are literal -- a search for "100%" finds "100%".
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Process name as recorded, matched case-insensitively.</summary>
    public string? ProcessName { get; init; }

    public DictationMode? Mode { get; init; }

    /// <summary>Filter on the bypass decision. Null means both.</summary>
    public bool? Bypassed { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}
