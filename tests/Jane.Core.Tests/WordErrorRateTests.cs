using Jane.Core.Text;

namespace Jane.Core.Tests;

public sealed class WordErrorRateTests
{
    [Fact]
    public void IdenticalText_ScoresZero()
    {
        Assert.Equal(0, WordErrorRate.Rate("the quick brown fox", "the quick brown fox"));
    }

    [Fact]
    public void MatchesAHandCheckedReference()
    {
        // Reference: "the quick brown fox jumps" (5 words)
        // Hypothesis: "the quick brown box jumped over" -> 2 substitutions, 1 insertion = 3/5.
        var counts = WordErrorRate.Compute("the quick brown fox jumps", "the quick brown box jumped over");

        Assert.Equal(2, counts.Substitutions);
        Assert.Equal(0, counts.Deletions);
        Assert.Equal(1, counts.Insertions);
        Assert.Equal(5, counts.ReferenceWords);
        Assert.Equal(0.6, counts.Rate, 6);
    }

    [Fact]
    public void CountsDeletionsSeparatelyFromSubstitutions()
    {
        // The distinction matters: a model that drops the ends of sentences behaves very
        // differently from one that mishears words, and a single edit distance would hide it.
        var counts = WordErrorRate.Compute("send it on tuesday please", "send it on tuesday");

        Assert.Equal(1, counts.Deletions);
        Assert.Equal(0, counts.Substitutions);
        Assert.Equal(0, counts.Insertions);
    }

    [Fact]
    public void NormalisationFollowsTheLeaderboardConvention()
    {
        // Lower-cased and punctuation-stripped, so numbers are comparable to published ones --
        // punctuation and casing are then scored as their own metrics rather than lost.
        Assert.Equal(0, WordErrorRate.Rate("Send it, Wednesday!", "send it wednesday"));
    }

    [Fact]
    public void ContractionsAreOneWord()
    {
        // Splitting "don't" into two tokens would invent an insertion on every contraction, and
        // dictation is full of them.
        Assert.Equal(["i", "don't", "think", "so"], WordErrorRate.Normalize("I don't think so."));
    }

    [Fact]
    public void EmptyHypothesisAgainstNonEmptyReferenceIsTotalFailure()
    {
        Assert.Equal(1, WordErrorRate.Rate("hello there", ""));
    }

    [Fact]
    public void EmptyAgainstEmptyIsPerfect()
    {
        Assert.Equal(0, WordErrorRate.Rate("", ""));
    }

    [Fact]
    public void PunctuationAccuracyRewardsMatchesAndPenalisesSpuriousMarks()
    {
        Assert.Equal(1, WordErrorRate.PunctuationAccuracy("Send it, Wednesday.", "Ship it, Wednesday."));

        // A missing sentence-final stop is the error users notice; it must not score 1.
        Assert.True(WordErrorRate.PunctuationAccuracy("Send it, Wednesday.", "Send it Wednesday") < 1);

        // A model sprinkling commas everywhere must not score perfectly either.
        Assert.True(WordErrorRate.PunctuationAccuracy("Send it Wednesday", "Send, it, Wednesday,") < 1);
    }

    [Fact]
    public void CasingAccuracyComparesOnlyWordsThatOtherwiseMatch()
    {
        // Words the recogniser got outright wrong are WER's problem. Mixing them in here would
        // make casing look bad whenever accuracy was bad, which measures nothing new.
        Assert.Equal(1, WordErrorRate.CasingAccuracy("Send it Wednesday", "Send it Wednesday"));
        Assert.True(WordErrorRate.CasingAccuracy("Send it Wednesday", "send it wednesday") < 1);
        Assert.Equal(1, WordErrorRate.CasingAccuracy("Send it Wednesday", "Send it Tuesday"));
    }

    [Fact]
    public void TermRecallMeasuresWhetherDictionaryTermsSurvived()
    {
        // This is the metric behind the Phase 10 acceptance: adding "Kubernetes" to the
        // dictionary must measurably raise its recall on the corpus.
        Assert.Equal(1, WordErrorRate.TermRecall("We run Kubernetes here.", ["Kubernetes"]));
        Assert.Equal(0, WordErrorRate.TermRecall("We run cooper netties here.", ["Kubernetes"]));
        Assert.Equal(0.5, WordErrorRate.TermRecall("Kubernetes and Docker", ["Kubernetes", "Cilium"]));
        Assert.Equal(1, WordErrorRate.TermRecall("anything", []));
    }

    [Fact]
    public void DescribeIsReadableInAReport()
    {
        var counts = WordErrorRate.Compute("the quick brown fox jumps", "the quick brown box jumped over");

        Assert.Contains("2S", WordErrorRate.Describe(counts), StringComparison.Ordinal);
        Assert.Contains("1I", WordErrorRate.Describe(counts), StringComparison.Ordinal);
    }
}
