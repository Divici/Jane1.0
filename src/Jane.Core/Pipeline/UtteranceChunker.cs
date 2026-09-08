using System.Text;
using Jane.Core.Abstractions;
using Jane.Core.Text;

namespace Jane.Core.Pipeline;

/// <summary>
/// Cuts a long utterance into recogniser-sized pieces, at pauses the detector already found.
/// </summary>
/// <remarks>
/// <para>
/// A capture is capped at five minutes and used to reach the recogniser as a single call. Offline
/// sherpa-onnx models are trained and evaluated on utterances of a few seconds; accuracy falls
/// away on multi-minute inputs, and the documented approach for long audio is to segment on voice
/// activity and recognise each segment. Nothing here decides <em>where</em> the pauses are -- that
/// is the voice-activity gate's job -- it only decides which of them to cut at.
/// </para>
/// <para>
/// One rule governs the whole thing: never cut inside speech. A cut through a word turns one right
/// word into two wrong ones, and neither the recogniser nor the formatter has any way to tell that
/// is what happened. When the budget and that rule conflict, the rule wins and the chunk is
/// oversized -- somebody who talks for two minutes without drawing breath gets a worse transcript
/// than they might have, rather than a mangled one.
/// </para>
/// </remarks>
public static class UtteranceChunker
{
    /// <summary>
    /// Above this, a single recogniser call is long enough to be worth splitting.
    /// </summary>
    /// <remarks>
    /// Thirty seconds is also what the voice-activity detector uses as its own per-segment
    /// ceiling, so the two agree about what "long" means.
    /// </remarks>
    public static readonly TimeSpan DefaultMaxChunk = TimeSpan.FromSeconds(30);

    /// <param name="segments">Speech runs, in the coordinates of the buffer being chunked.</param>
    /// <param name="totalSamples">Length of that buffer.</param>
    /// <param name="maxChunkSamples">Budget per chunk, yielded to only by an oversized segment.</param>
    /// <returns>
    /// Chunks in order, covering every sample of speech, never overlapping. A single chunk
    /// spanning the whole buffer when there is nothing to gain from splitting -- which keeps the
    /// ordinary one-sentence dictation on exactly the path it was on before.
    /// </returns>
    public static IReadOnlyList<SpeechSpan> Chunk(
        IReadOnlyList<SpeechSpan> segments,
        int totalSamples,
        int maxChunkSamples)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // Nothing to cut on, or nothing worth cutting. Both hand back the buffer unchanged, which
        // is what every dictation short of a monologue gets.
        if (segments.Count <= 1 || totalSamples <= maxChunkSamples || maxChunkSamples <= 0)
        {
            return [new SpeechSpan(0, totalSamples)];
        }

        List<SpeechSpan> chunks = [];
        var chunkStart = 0;
        var chunkEnd = segments[0].End;

        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];

            if (segment.End - chunkStart <= maxChunkSamples)
            {
                chunkEnd = segment.End;
                continue;
            }

            // Close the chunk at the last segment that fitted, and start the next one at this
            // segment. The silence between them is the cut, and it belongs to neither.
            chunks.Add(new SpeechSpan(chunkStart, chunkEnd - chunkStart));
            chunkStart = segment.Start;
            chunkEnd = segment.End;
        }

        // The last chunk runs to the end of the buffer rather than to the last segment, so the
        // trailing padding the gate kept is not thrown away here.
        chunks.Add(new SpeechSpan(chunkStart, Math.Max(chunkEnd, totalSamples) - chunkStart));
        return chunks;
    }

    /// <summary>
    /// Joins per-chunk results into one, rebasing every word timing onto the whole utterance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text is joined the way two consecutive dictations into one window are, so a chunk
    /// boundary reads as the sentence boundary it actually is rather than as <c>part.Second</c>.
    /// </para>
    /// <para>
    /// The rebasing is the part that would go wrong silently. Each recogniser call reports word
    /// times relative to the slice it was given, so a word two minutes into a dictation comes back
    /// at three seconds. Nothing downstream could detect that, which is why it is done here, in one
    /// place, against the chunk offsets that produced it.
    /// </para>
    /// </remarks>
    public static RecognitionResult Join(
        IReadOnlyList<RecognitionResult> results, IReadOnlyList<SpeechSpan> chunks)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(chunks);

        if (results.Count == 1)
        {
            return results[0];
        }

        var text = new StringBuilder();
        List<WordTiming> words = [];
        var timings = RecognitionTimings.Zero;
        var method = string.Empty;

        for (var i = 0; i < results.Count; i++)
        {
            var piece = results[i];
            var offset = i < chunks.Count ? AudioFormat.DurationOf(chunks[i].Start).TotalSeconds : 0;

            if (!piece.IsEmpty)
            {
                var joined = text.Length == 0
                    ? piece.Text
                    : SpacingPolicy.Apply(piece.Text, text.ToString());
                text.Append(joined);
            }

            foreach (var word in piece.Words)
            {
                words.Add(word with { Start = word.Start + offset, End = word.End + offset });
            }

            timings += piece.Timings;
            method = piece.DecodingMethod;
        }

        return new RecognitionResult(text.ToString(), words, timings, method);
    }
}
