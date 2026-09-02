using Jane.Core.Abstractions;
using Jane.Core.Audio;
using Jane.Core.Platform;
using Jane.Speech;

namespace Jane.Speech.Tests;

/// <summary>
/// The VAD's arithmetic -- what gets trimmed, what counts as speech, what is rejected -- tested
/// without the ONNX file, plus a smoke test that runs only when the real model is present.
/// </summary>
/// <remarks>
/// Splitting it this way is deliberate: the trimming rules are where the bugs live and where a
/// regression would silently clip a user's first word, and they must be assertable on a machine
/// that has never downloaded a model.
/// </remarks>
public sealed class SileroVadGateTests
{
    private static readonly VadOptions Options = new()
    {
        Padding = TimeSpan.FromMilliseconds(100),
        MergeGap = TimeSpan.FromMilliseconds(300),
        MinimumUtterance = TimeSpan.FromMilliseconds(300),
    };

    private static int Samples(int milliseconds) =>
        AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(milliseconds));

    private static float[] Ramp(int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = i;
        }

        return samples;
    }

    private static VadSegment Segment(int startMs, int durationMs) =>
        new(Samples(startMs), Samples(durationMs));

    // Tests run out of bin/Debug/<tfm>, so walk up to the repository root to find the corpus.
    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? name
            : Path.Combine(directory.FullName, "tests", "fixtures", "audio", name);
    }

    [Fact]
    public void MissingModelRaisesATypedErrorRatherThanSilentlyDisablingVad()
    {
        // OpenWhispr#1057: a VAD that quietly turns itself off when its model is absent looks
        // like a transcription-quality problem for weeks before anyone finds the real cause.
        var missing = Path.Combine(Path.GetTempPath(), "jane-tests", "definitely-not-here.onnx");

        var error = Assert.Throws<VadModelMissingException>(() => new SileroVadGate(missing));

        Assert.Equal(missing, error.ModelPath);
        Assert.Contains("silero", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrimsLeadingAndTrailingSilence()
    {
        // Three seconds of buffer, one second of speech in the middle of it.
        var buffer = Ramp(Samples(3_000));

        var result = SileroVadGate.Trim(buffer, [Segment(1_000, 1_000)], Options);

        Assert.True(result.ContainsSpeech);
        Assert.Equal(Samples(900), result.LeadingTrimmedSamples);
        Assert.Equal(Samples(1_200), result.Samples.Length);

        // Padding is kept, so the trimmed buffer starts 100 ms before the speech did.
        Assert.Equal(Samples(900), result.Samples.Span[0]);
    }

    [Fact]
    public void FlagsAnEmptyUtteranceWhenThereIsNoSpeechAtAll()
    {
        var result = SileroVadGate.Trim(Ramp(Samples(2_000)), [], Options);

        Assert.False(result.ContainsSpeech);
        Assert.Equal(0, result.Samples.Length);
        Assert.Equal(TimeSpan.Zero, result.SpeechDuration);
    }

    [Fact]
    public void RejectsAnUtteranceShorterThanTheMinimum()
    {
        // "No model work begins until the VAD confirms 300 ms of speech" is the rule that keeps
        // a stray key brush from spinning up an ASR pass, and this is where it is enforced.
        var result = SileroVadGate.Trim(Ramp(Samples(2_000)), [Segment(500, 150)], Options);

        Assert.False(result.ContainsSpeech);
        Assert.Equal(0, result.Samples.Length);
        Assert.Equal(150, result.SpeechDuration.TotalMilliseconds, tolerance: 1.0);
    }

    [Fact]
    public void MergesSegmentsSeparatedByLessThanTheGap()
    {
        // Silero splits on short pauses inside a single spoken phrase. Left unmerged, a
        // 200 ms breath between two 200 ms fragments would fail the minimum-utterance gate even
        // though the user clearly said something.
        var result = SileroVadGate.Trim(
            Ramp(Samples(3_000)),
            [Segment(500, 200), Segment(900, 200)],
            Options);

        Assert.True(result.ContainsSpeech);
        Assert.Equal([new VadSegment(Samples(500), Samples(600))], result.Segments);
        Assert.Equal(600, result.SpeechDuration.TotalMilliseconds, tolerance: 1.0);
    }

    [Fact]
    public void KeepsInteriorSilenceBetweenSeparateSentences()
    {
        // A one-second pause between two sentences is meaningful -- it is where a full stop
        // goes -- so trimming is only ever done at the ends.
        var result = SileroVadGate.Trim(
            Ramp(Samples(5_000)),
            [Segment(500, 500), Segment(2_500, 500)],
            Options);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(Samples(400), result.LeadingTrimmedSamples);
        Assert.Equal(Samples(2_700), result.Samples.Length);
        Assert.Equal(1_000, result.SpeechDuration.TotalMilliseconds, tolerance: 1.0);
    }

    [Fact]
    public void PaddingNeverRunsPastEitherEndOfTheBuffer()
    {
        var buffer = Ramp(Samples(1_000));

        var result = SileroVadGate.Trim(buffer, [new VadSegment(0, buffer.Length)], Options);

        Assert.Equal(0, result.LeadingTrimmedSamples);
        Assert.Equal(buffer.Length, result.Samples.Length);
    }

    [Fact]
    public void SegmentsAreReportedInOriginalBufferCoordinates()
    {
        // The trimmed buffer is what downstream consumes, but the offsets stay in the original
        // frame so LeadingTrimmedSamples is the single, unambiguous way to line them up.
        var result = SileroVadGate.Trim(Ramp(Samples(3_000)), [Segment(1_000, 800)], Options);

        Assert.Equal(Samples(1_000), result.Segments[0].StartSample);
        Assert.Equal(Samples(1_800), result.Segments[0].EndSample);
        Assert.Equal(
            result.Segments[0].StartSample,
            result.LeadingTrimmedSamples + Samples(100));
    }

    [Fact]
    public void ASegmentRunningPastTheBufferIsClampedRatherThanThrowing()
    {
        // sherpa reports a segment start plus a sample count; a flush at the very end of a
        // capture can round past the buffer it was given.
        var buffer = Ramp(Samples(1_000));

        var result = SileroVadGate.Trim(buffer, [new VadSegment(Samples(400), Samples(800))], Options);

        Assert.True(result.ContainsSpeech);
        Assert.Equal(buffer.Length, result.LeadingTrimmedSamples + result.Samples.Length);
    }

    [Fact]
    public void RealModelReportsSilenceAsAnEmptyUtterance()
    {
        // Runs only where Phase 1's downloader has already fetched the weights. Silence is the
        // one input whose expected verdict is certain regardless of model version.
        var modelPath = ModelCatalog.SileroVad.ResolvePath(new JanePaths().Models);
        Assert.SkipUnless(File.Exists(modelPath), $"Silero VAD model not downloaded at {modelPath}.");

        using var gate = new SileroVadGate(modelPath);

        var result = gate.Process(new float[AudioFormat.SampleRate * 2]);

        Assert.False(result.ContainsSpeech);
        Assert.Equal(0, result.Samples.Length);
    }

    [Fact]
    public void RealModelFindsSpeechInAFixtureAndTrimsTheSilenceAroundIt()
    {
        var modelPath = ModelCatalog.SileroVad.ResolvePath(new JanePaths().Models);
        var fixture = FixturePath("prose-01.wav");
        Assert.SkipUnless(
            File.Exists(modelPath) && File.Exists(fixture),
            "Silero VAD model or the fixture corpus is not present.");

        var samples = WaveFile.Read(fixture);
        using var gate = new SileroVadGate(modelPath);

        var result = gate.Process(samples);

        Assert.True(result.ContainsSpeech);
        Assert.NotEmpty(result.Segments);

        // The corpus clips are TTS renders that begin and end in silence, so a working gate must
        // return strictly less audio than it was handed.
        Assert.True(
            result.Samples.Length < samples.Length,
            $"Trimmed {result.Samples.Length} of {samples.Length} samples -- nothing was cut.");
        Assert.True(result.LeadingTrimmedSamples > 0);
        Assert.True(result.SpeechDuration >= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void RealModelIsReusableAcrossConsecutiveUtterances()
    {
        var modelPath = ModelCatalog.SileroVad.ResolvePath(new JanePaths().Models);
        Assert.SkipUnless(File.Exists(modelPath), $"Silero VAD model not downloaded at {modelPath}.");

        using var gate = new SileroVadGate(modelPath);

        // A detector that leaked state between dictations would report the previous utterance's
        // segments against this one's buffer.
        for (var run = 0; run < 3; run++)
        {
            Assert.False(gate.Process(new float[AudioFormat.SampleRate]).ContainsSpeech);
        }
    }
}
