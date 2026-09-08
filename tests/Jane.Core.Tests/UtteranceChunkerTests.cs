using Jane.Core.Abstractions;
using Jane.Core.Pipeline;

namespace Jane.Core.Tests;

/// <summary>
/// Splitting a long dictation at the pauses, so the recogniser never sees minutes in one call.
/// </summary>
/// <remarks>
/// <para>
/// A capture is capped at five minutes and the whole buffer went to the recogniser in a single
/// call. Offline sherpa-onnx models are trained and evaluated on utterances of a few seconds;
/// accuracy degrades on multi-minute inputs, and the documented approach for long audio is to
/// segment on voice activity and recognise each segment. The reported dictation was long enough
/// to be in that territory.
/// </para>
/// <para>
/// The cuts land in silence the detector already found, never inside speech. That matters twice
/// over: a cut through a word invents two wrong words out of one right one, and the pause is
/// where the full stop belongs anyway.
/// </para>
/// </remarks>
public sealed class UtteranceChunkerTests
{
    private static int Seconds(double value) => AudioFormat.SamplesFor(TimeSpan.FromSeconds(value));

    private static readonly int MaxChunk = Seconds(30);

    [Fact]
    public void ShortAudioIsOneChunkAndTheBufferIsNotTouched()
    {
        // The overwhelmingly common case: a sentence or two. Nothing about it should change.
        var chunks = UtteranceChunker.Chunk(
            [new SpeechSpan(Seconds(0.2), Seconds(3))], totalSamples: Seconds(4), MaxChunk);

        var only = Assert.Single(chunks);
        Assert.Equal(0, only.Start);
        Assert.Equal(Seconds(4), only.Length);
    }

    [Fact]
    public void AudioWithNoDetectedSpeechIsStillOneChunk()
    {
        // Nothing to cut on. Handing the whole buffer over is what the code did before, and is
        // the right fallback when the detector found no boundaries to use.
        var chunks = UtteranceChunker.Chunk([], totalSamples: Seconds(90), MaxChunk);

        var only = Assert.Single(chunks);
        Assert.Equal(Seconds(90), only.Length);
    }

    [Fact]
    public void ALongDictationIsCutAtThePauses()
    {
        // Four ten-second phrases with a pause after each. The budget fits three.
        List<SpeechSpan> segments =
        [
            new(Seconds(0), Seconds(10)),
            new(Seconds(11), Seconds(10)),
            new(Seconds(22), Seconds(10)),
            new(Seconds(33), Seconds(10)),
        ];

        var chunks = UtteranceChunker.Chunk(segments, totalSamples: Seconds(44), MaxChunk);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(Seconds(21), chunks[0].End);
        Assert.Equal(Seconds(22), chunks[1].Start);
        Assert.Equal(Seconds(44), chunks[1].End);
    }

    [Fact]
    public void NoChunkEverStartsOrEndsInsideSpeech()
    {
        // The rule that makes this safe. A cut through a word turns one right word into two wrong
        // ones, and the recogniser has no way to tell that is what happened.
        List<SpeechSpan> segments = [];
        for (var i = 0; i < 12; i++)
        {
            segments.Add(new SpeechSpan(Seconds(i * 11), Seconds(9)));
        }

        var chunks = UtteranceChunker.Chunk(segments, totalSamples: Seconds(132), MaxChunk);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            foreach (var segment in segments)
            {
                var straddlesStart = segment.Start < chunk.Start && segment.End > chunk.Start;
                var straddlesEnd = segment.Start < chunk.End && segment.End > chunk.End;
                Assert.False(straddlesStart || straddlesEnd, $"Chunk {chunk} cuts through {segment}.");
            }
        }
    }

    [Fact]
    public void TheChunksCoverEverySampleOfSpeech()
    {
        List<SpeechSpan> segments =
        [
            new(Seconds(1), Seconds(20)),
            new(Seconds(25), Seconds(20)),
            new(Seconds(50), Seconds(20)),
        ];

        var chunks = UtteranceChunker.Chunk(segments, totalSamples: Seconds(75), MaxChunk);

        foreach (var segment in segments)
        {
            Assert.Contains(chunks, c => c.Start <= segment.Start && c.End >= segment.End);
        }
    }

    [Fact]
    public void ASingleSegmentLongerThanTheBudgetIsKeptWhole()
    {
        // Somebody talking for two minutes without a pause the detector can see. Splitting inside
        // that is the one thing not allowed, so the budget yields instead.
        var chunks = UtteranceChunker.Chunk(
            [new SpeechSpan(0, Seconds(120))], totalSamples: Seconds(121), MaxChunk);

        var only = Assert.Single(chunks);
        Assert.True(only.Length >= Seconds(120));
    }

    [Fact]
    public void ChunksAreOrderedAndDoNotOverlap()
    {
        List<SpeechSpan> segments = [];
        for (var i = 0; i < 20; i++)
        {
            segments.Add(new SpeechSpan(Seconds(i * 8), Seconds(7)));
        }

        var chunks = UtteranceChunker.Chunk(segments, totalSamples: Seconds(160), MaxChunk);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Start >= chunks[i - 1].End, "Chunks must not overlap.");
        }
    }
}
