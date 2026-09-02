using Jane.Core.Abstractions;
using Jane.Core.Formatting;

namespace Jane.Core.Tests;

/// <summary>
/// The bypass is the only place Jane deliberately ignores the user's own settings, so every
/// condition that forbids it is asserted individually.
/// </summary>
/// <remarks>
/// BLOCKER #9 in plan.md: an earlier design bypassed whenever the transcript "looked
/// well-punctuated", which — since Parakeet punctuates natively — would have fired on almost
/// every dictation and silently skipped the dictionary, Custom Instructions, filler removal and
/// self-correction resolution. These tests exist to keep that from creeping back.
/// </remarks>
public sealed class BypassHeuristicTests
{
    private static readonly FormattingContext Notepad = new("notepad");

    [Fact]
    public void CleanShortTranscriptBypasses()
    {
        var decision = new BypassHeuristic().Evaluate("send it wednesday", Notepad, FormattingPolicy.None);

        Assert.True(decision.Bypassed);
        Assert.Empty(decision.Blockers);
        Assert.Equal(BypassBlocker.None, decision.PrimaryBlocker);
    }

    [Fact]
    public void FillerWordBlocksBypass()
    {
        var decision = new BypassHeuristic().Evaluate("um send it wednesday", Notepad, FormattingPolicy.None);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Lexicon, decision.Blockers);
        Assert.Contains("um", decision.LexiconHits);
    }

    [Fact]
    public void SelfCorrectionPhraseBlocksBypass()
    {
        // Multi-word markers are the whole point: "no wait" is invisible to a single-token scan.
        var decision = new BypassHeuristic().Evaluate("send it tuesday no wait wednesday", Notepad, FormattingPolicy.None);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Lexicon, decision.Blockers);
        Assert.Contains("no wait", decision.LexiconHits);
    }

    [Theory]
    [InlineData("uh")]
    [InlineData("scratch that")]
    [InlineData("I mean")]
    public void EveryLexiconMarkerNamedInThePlanBlocksBypass(string marker)
    {
        var decision = new BypassHeuristic().Evaluate($"send it {marker} wednesday", Notepad, FormattingPolicy.None);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Lexicon, decision.Blockers);
    }

    [Fact]
    public void LexiconMatchesWholeWordsOnly()
    {
        // "um" inside "album" and "uh" inside "though" must not cost every dictation the LLM.
        var decision = new BypassHeuristic().Evaluate("the album though", Notepad, FormattingPolicy.None);

        Assert.True(decision.Bypassed);
    }

    [Fact]
    public void LongTranscriptBlocksBypass()
    {
        var options = new BypassOptions { MaxWords = 6 };
        var decision = new BypassHeuristic(options).Evaluate(
            "one two three four five six seven", Notepad, FormattingPolicy.None);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Length, decision.Blockers);
        Assert.Equal(7, decision.WordCount);
    }

    [Fact]
    public void CustomInstructionsForTheFocusedAppBlockBypass()
    {
        var policy = new FormattingPolicy { CustomInstructions = "Always sign off with my initials." };

        var decision = new BypassHeuristic().Evaluate("send it wednesday", Notepad, policy);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.CustomInstructions, decision.Blockers);
    }

    [Fact]
    public void PendingDictionaryReplacementBlocksBypass()
    {
        var policy = new FormattingPolicy
        {
            Replacements = [new DictionaryReplacement("kubernetes", "Kubernetes")],
        };

        var decision = new BypassHeuristic().Evaluate("ship it to kubernetes", Notepad, policy);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.DictionaryReplacement, decision.Blockers);
    }

    [Fact]
    public void VocabularyTermsAloneDoNotBlockBypass()
    {
        // Terms only help the model spell things; with nothing to substitute there is no work
        // for the LLM to do, so they must not become a permanent bypass veto.
        var policy = new FormattingPolicy { Terms = ["Kubernetes", "Postgres"] };

        var decision = new BypassHeuristic().Evaluate("ship it wednesday", Notepad, policy);

        Assert.True(decision.Bypassed);
    }

    [Fact]
    public void PolicyCanSuppressBypassOutright()
    {
        var policy = new FormattingPolicy { SuppressBypass = true };

        var decision = new BypassHeuristic().Evaluate("send it wednesday", Notepad, policy);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Suppressed, decision.Blockers);
    }

    [Fact]
    public void DisablingTheHeuristicSuppressesBypass()
    {
        var decision = new BypassHeuristic(new BypassOptions { Enabled = false })
            .Evaluate("send it wednesday", Notepad, FormattingPolicy.None);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Suppressed, decision.Blockers);
    }

    [Fact]
    public void EveryBlockerIsRecordedNotJustTheFirst()
    {
        // Phase 12 measures the false-bypass rate off these records; a decision that stopped at
        // the first failing condition would make "why did this not bypass" unanswerable.
        var policy = new FormattingPolicy
        {
            CustomInstructions = "Be terse.",
            Replacements = [new DictionaryReplacement("kate", "Kate")],
        };

        var decision = new BypassHeuristic(new BypassOptions { MaxWords = 3 })
            .Evaluate("um tell kate about it no wait tell daniel", Notepad, policy);

        Assert.False(decision.Bypassed);
        Assert.Contains(BypassBlocker.Length, decision.Blockers);
        Assert.Contains(BypassBlocker.Lexicon, decision.Blockers);
        Assert.Contains(BypassBlocker.CustomInstructions, decision.Blockers);
        Assert.Contains(BypassBlocker.DictionaryReplacement, decision.Blockers);
    }

    [Fact]
    public void DecisionCarriesAHumanReadableReason()
    {
        var decision = new BypassHeuristic().Evaluate("um send it", Notepad, FormattingPolicy.None);

        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.Contains("um", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
