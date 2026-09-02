using System.Text.Json.Serialization;
using Jane.Core.Abstractions;

namespace Jane.Core.Formatting;

/// <summary>One reason the LLM could not be skipped. Every applicable one is recorded.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BypassBlocker>))]
public enum BypassBlocker
{
    /// <summary>Placeholder for a decision with no blockers at all.</summary>
    None,

    /// <summary>The bypass is switched off, globally or for this dictation.</summary>
    Suppressed,

    /// <summary>Long enough that structure -- paragraphs, lists -- is plausibly needed.</summary>
    Length,

    /// <summary>Filler or a self-correction marker was spoken.</summary>
    Lexicon,

    /// <summary>The focused app has Custom Instructions that the raw text would ignore.</summary>
    CustomInstructions,

    /// <summary>A dictionary entry would rewrite something in this transcript.</summary>
    DictionaryReplacement,

    /// <summary>
    /// The transcript contains a term the user put in their dictionary. Added after the eval
    /// measured a false bypass on exactly this case; see BypassHeuristic for the reasoning.
    /// </summary>
    DictionaryTerm,
}

/// <param name="MaxWords">
/// Above this the transcript is long enough that inferred structure is worth an LLM call. Short
/// is the operative word in the plan's rule; twelve words is roughly one spoken sentence.
/// </param>
/// <param name="Enabled">
/// The global kill switch. Off means every dictation goes through the LLM, which is what the eval
/// harness uses to measure what the bypass would have skipped.
/// </param>
public sealed record BypassOptions
{
    public bool Enabled { get; init; } = true;

    public int MaxWords { get; init; } = 12;
}

/// <summary>
/// Why one dictation did or did not skip the LLM.
/// </summary>
/// <remarks>
/// A bool would have been enough to run the pipeline and useless for everything else. This is
/// written to history on every dictation so Phase 12 can re-run the skipped transcripts through
/// the LLM and report a false-bypass rate, and so "why did Jane not apply my dictionary here"
/// has an answer.
/// </remarks>
/// <param name="Bypassed">True means the LLM was skipped and raw ASR text was injected.</param>
/// <param name="Blockers">
/// Every condition that forbade the bypass, not just the first. Stopping at the first would make
/// the false-bypass analysis unable to tell a transcript that failed one condition from one that
/// failed four.
/// </param>
/// <param name="Reason">One line, written for a person reading their own history.</param>
public sealed record BypassDecision(
    bool Bypassed,
    IReadOnlyList<BypassBlocker> Blockers,
    string Reason,
    int WordCount,
    IReadOnlyList<string> LexiconHits)
{
    public BypassBlocker PrimaryBlocker => Blockers.Count == 0 ? BypassBlocker.None : Blockers[0];
}

/// <summary>
/// Decides whether a transcript can skip the LLM entirely.
/// </summary>
/// <remarks>
/// <para>
/// Every condition must hold. The earlier design bypassed when the transcript "looked
/// well-punctuated", which -- since Parakeet emits punctuation and casing natively -- was true of
/// almost every dictation; the bypass would have fired constantly and silently skipped filler
/// removal, self-corrections, the user's dictionary and their Custom Instructions. That is
/// BLOCKER #9 in plan.md, and it is why this class ANDs four conditions instead of ORing
/// heuristics.
/// </para>
/// <para>
/// The asymmetry is deliberate throughout: failing to bypass costs one LLM call, wrongly
/// bypassing costs the user text they explicitly retracted. Everything ambiguous resolves
/// towards running the LLM.
/// </para>
/// </remarks>
public sealed class BypassHeuristic(BypassOptions? options = null)
{
    private readonly BypassOptions _options = options ?? new BypassOptions();

    public BypassOptions Options => _options;

    public BypassDecision Evaluate(string transcript, FormattingContext context, FormattingPolicy policy)
    {
        var blockers = new List<BypassBlocker>();
        var reasons = new List<string>();

        if (!_options.Enabled || policy.SuppressBypass)
        {
            blockers.Add(BypassBlocker.Suppressed);
            reasons.Add("the bypass is switched off");
        }

        var words = FormattingLexicon.CountWords(transcript);
        if (words > _options.MaxWords)
        {
            blockers.Add(BypassBlocker.Length);
            reasons.Add($"{words} words is over the {_options.MaxWords}-word bypass limit");
        }

        var hits = FormattingLexicon.FindHits(transcript);
        if (hits.Count > 0)
        {
            blockers.Add(BypassBlocker.Lexicon);
            reasons.Add($"spoken filler or self-correction: {string.Join(", ", hits)}");
        }

        if (policy.HasCustomInstructions)
        {
            var app = string.IsNullOrEmpty(context.TargetProcessName) ? "this app" : context.TargetProcessName;
            blockers.Add(BypassBlocker.CustomInstructions);
            reasons.Add($"Custom Instructions are active for {app}");
        }

        if (policy.Replacements.Count > 0)
        {
            blockers.Add(BypassBlocker.DictionaryReplacement);
            reasons.Add($"{policy.Replacements.Count} pending dictionary replacement(s)");
        }

        // A dictionary *term* blocks the bypass too, not only a replacement.
        //
        // This was added after the eval measured it. The `code-02` fixture -- "the config lives in
        // source, jane dot core, settings, settings store dot C S" -- is ten clean words with no
        // filler, so every other condition passed and the bypass fired, injecting
        // "jane.core, settings, settings store.cs". Forcing the LLM produced
        // "Jane.Core, Settings, SettingsStore.cs." The user had put those exact identifiers in
        // their dictionary, and the bypass skipped the only stage that could act on them.
        //
        // Terms were originally treated as spelling hints that never block, on the reasoning that
        // they change nothing on their own. That is true of the prompt, and false of the outcome:
        // the reason a term is in the dictionary at all is that the user wants it written a
        // particular way. The plan's own asymmetry settles it -- a wrong block costs one ~400 ms
        // call, a wrong bypass silently ships text the user had already told Jane how to write.
        var matchedTerms = policy.Terms
            .Where(term => FormattingLexicon.ContainsWholeWord(transcript, term))
            .ToArray();

        if (matchedTerms.Length > 0)
        {
            blockers.Add(BypassBlocker.DictionaryTerm);
            reasons.Add($"the transcript contains dictionary term(s) {string.Join(", ", matchedTerms)}");
        }

        return blockers.Count == 0
            ? new BypassDecision(true, [], $"Bypassed: {words} clean words, no instructions, no dictionary terms, no replacements.", words, hits)
            : new BypassDecision(false, blockers, $"Formatted because {string.Join("; ", reasons)}.", words, hits);
    }
}
