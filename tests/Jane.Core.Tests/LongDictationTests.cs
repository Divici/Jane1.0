using Jane.Core.Abstractions;
using Jane.Core.Pipeline;

namespace Jane.Core.Tests;

/// <summary>
/// A dictation that runs for minutes reaches the recogniser in pieces, cut at the pauses.
/// </summary>
/// <remarks>
/// The end-to-end half of <see cref="UtteranceChunkerTests"/>: that one pins where the cuts go,
/// this one pins that the orchestrator makes them, joins the pieces into one transcript, and keeps
/// the word timings pointing at the right moments of the whole utterance rather than of each
/// chunk.
/// </remarks>
public sealed class LongDictationTests
{
    private static int Seconds(double value) => AudioFormat.SamplesFor(TimeSpan.FromSeconds(value));

    [Fact]
    public async Task AnOrdinaryDictationIsStillOneCall()
    {
        // The common case must not get slower or subtly different to serve the rare one.
        var harness = new Harness();

        await harness.DictateAsync("hello world");

        Assert.Equal(1, harness.Recognizer.Calls);
        Assert.Equal("hello world", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task AFourMinuteDictationIsRecognisedInPieces()
    {
        var harness = LongHarness();

        await harness.DictateAsync();

        Assert.True(
            harness.Recognizer.Calls > 1,
            $"A {harness.Source.CaptureLength.TotalSeconds:F0}s utterance should not go to the recogniser in one call.");
    }

    [Fact]
    public async Task NoPieceIsLongerThanTheBudget()
    {
        // The whole point: an offline model is trained on utterances of a few seconds, and this is
        // what keeps every call inside that range.
        var harness = LongHarness();

        await harness.DictateAsync();

        var budget = Seconds(30);
        Assert.All(harness.Recognizer.SampleCounts, count => Assert.True(
            count <= budget,
            $"A chunk of {AudioFormat.DurationOf(count).TotalSeconds:F0}s is over the 30s budget."));
    }

    [Fact]
    public async Task ThePiecesAreJoinedIntoOneTranscriptWithSentenceSpacing()
    {
        var harness = LongHarness();
        harness.Recognizer.PerCall.AddRange(["First part.", "Second part.", "Third part."]);

        await harness.DictateAsync();

        var injected = Assert.Single(harness.Injector.Injected);
        Assert.StartsWith("First part.", injected, StringComparison.Ordinal);
        Assert.Contains("Second part.", injected, StringComparison.Ordinal);

        // Joined the way two consecutive dictations into one window are, so a chunk boundary reads
        // as the sentence boundary it is rather than as "part.Second".
        Assert.DoesNotContain("part.Second", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void WordTimingsAreRebasedOntoTheWholeUtterance()
    {
        // Per-chunk offsets would silently put every word after the first boundary in the wrong
        // place, and nothing downstream could tell. Each piece reports its word at 0.1s into its
        // own slice; joined, they must be spread across the utterance instead of stacked.
        var chunks = new[] { new SpeechSpan(0, Seconds(30)), new SpeechSpan(Seconds(31), Seconds(30)) };
        var pieces = new[] { Piece("first", "one"), Piece("second", "two") };

        var joined = UtteranceChunker.Join(pieces, chunks);

        Assert.Equal(2, joined.Words.Count);
        Assert.Equal(0.1, joined.Words[0].Start, precision: 3);
        Assert.Equal(31.1, joined.Words[1].Start, precision: 3);
    }

    [Fact]
    public void JoinedTimingsAreTheSumOfThePiecesRatherThanTheLast()
    {
        // The history row records what a dictation cost. Reporting only the final chunk's decode
        // time would make a four-minute utterance look like a three-second one.
        var chunks = new[] { new SpeechSpan(0, Seconds(30)), new SpeechSpan(Seconds(31), Seconds(30)) };
        var pieces = new[] { Piece("a", "x"), Piece("b", "y") };

        var joined = UtteranceChunker.Join(pieces, chunks);

        Assert.Equal(TimeSpan.FromMilliseconds(160), joined.Timings.Decode);
    }

    private static RecognitionResult Piece(string text, string word) =>
        new(
            text,
            [new WordTiming(word, 0.1, 0.4)],
            new RecognitionTimings(
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80)),
            "greedy_search");

    [Fact]
    public async Task ADictationWithNoDetectedSegmentsIsStillHandledInOneCall()
    {
        // No cut points to use. Handing the whole buffer over is what happened before and is the
        // right fallback -- a worse transcript beats no transcript.
        var harness = new Harness { Segments = [] };
        harness.Source.CaptureLength = TimeSpan.FromMinutes(2);

        await harness.DictateAsync("one long stretch");

        Assert.Equal(1, harness.Recognizer.Calls);
    }

    /// <summary>Four minutes of speech in ten-second phrases, with a one-second pause after each.</summary>
    private static Harness LongHarness()
    {
        List<SpeechSpan> segments = [];
        for (var i = 0; i < 24; i++)
        {
            segments.Add(new SpeechSpan(Seconds(i * 11), Seconds(10)));
        }

        var harness = new Harness { Segments = segments };
        harness.Source.CaptureLength = TimeSpan.FromSeconds(264);
        return harness;
    }
}
