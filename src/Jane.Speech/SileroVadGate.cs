using Jane.Core.Abstractions;
using SherpaOnnx;

namespace Jane.Speech;

/// <summary>
/// The VAD model is not on disk.
/// </summary>
/// <remarks>
/// Typed, and thrown rather than swallowed, because the alternative -- carrying on with voice
/// activity detection quietly disabled -- is the failure mode behind <c>OpenWhispr#1057</c>:
/// dictation still "works", every utterance simply carries its leading and trailing silence into
/// the recogniser, and the symptom reads as a model-quality problem for weeks.
/// </remarks>
public sealed class VadModelMissingException : Exception
{
    public VadModelMissingException(string modelPath)
        : base($"The Silero VAD model was not found at '{modelPath}'. Voice activity detection cannot run, and Jane will not fall back to processing untrimmed audio.") =>
        ModelPath = modelPath;

    public VadModelMissingException(string modelPath, Exception innerException)
        : base($"The Silero VAD model at '{modelPath}' could not be loaded.", innerException) =>
        ModelPath = modelPath;

    public string ModelPath { get; }
}

/// <param name="Threshold">Silero's speech probability cut-off. Higher rejects more.</param>
/// <param name="MinSpeech">Shortest run Silero will call speech at all.</param>
/// <param name="MinSilence">Quiet needed before Silero closes a segment.</param>
/// <param name="Padding">
/// Audio kept after the detected speech. A hard cut on the exact boundary shaves the tail off the
/// last phoneme, which reads to the recogniser as a different word.
/// </param>
/// <param name="LeadingPadding">
/// Audio kept before the detected speech, and deliberately larger than <paramref name="Padding"/>.
/// </param>
/// <remarks>
/// The two ends are not symmetric, because the detector is not. Silero decides on 32 ms frames
/// and needs the probability to clear <paramref name="Threshold"/>, so the sample it calls the
/// start of speech is one to three frames after speech actually started -- and on a quiet onset,
/// a soft "the" or "I", more. Trimming to that boundary plus 100 ms was still cutting into the
/// first word, which is one of the two causes behind the clipped-first-word report; the other was
/// the microphone opening on key-down. 300 ms costs the recogniser a fifth of a second of leading
/// silence, which it is built to ignore.
/// </remarks>
/// <param name="MergeGap">
/// Segments closer than this become one utterance. Silero splits on breaths inside a single
/// phrase, and three 150 ms fragments would each fail <paramref name="MinimumUtterance"/>
/// despite the user plainly having said something.
/// </param>
/// <param name="MinimumUtterance">
/// Total speech below this is treated as no speech. This is the "arming is free" rule made
/// concrete: no model work happens until the VAD has confirmed real speech.
/// </param>
/// <param name="WindowSize">Silero's frame size in samples. 512 at 16 kHz is what the model expects.</param>
/// <param name="NumThreads">One is plenty: the VAD is milliseconds of work against seconds of audio.</param>
public sealed record VadOptions(
    float Threshold,
    TimeSpan MinSpeech,
    TimeSpan MinSilence,
    TimeSpan Padding,
    TimeSpan MergeGap,
    TimeSpan MinimumUtterance,
    int WindowSize,
    int NumThreads,
    TimeSpan LeadingPadding = default)
{
    public VadOptions()
        : this(
            Threshold: 0.5f,
            MinSpeech: TimeSpan.FromMilliseconds(250),
            MinSilence: TimeSpan.FromMilliseconds(250),
            Padding: TimeSpan.FromMilliseconds(100),
            MergeGap: TimeSpan.FromMilliseconds(300),
            MinimumUtterance: TimeSpan.FromMilliseconds(300),
            WindowSize: 512,
            NumThreads: 1,
            LeadingPadding: TimeSpan.FromMilliseconds(300))
    {
    }
}

/// <param name="StartSample">Offset into the buffer the VAD was given, not into the trimmed result.</param>
public readonly record struct VadSegment(int StartSample, int SampleCount)
{
    public int EndSample => StartSample + SampleCount;

    public TimeSpan Duration => AudioFormat.DurationOf(SampleCount);
}

/// <param name="Samples">The trimmed buffer, or empty when there was no speech.</param>
/// <param name="ContainsSpeech">
/// False both when nothing was detected and when what was detected fell under
/// <see cref="VadOptions.MinimumUtterance"/>. Either way the pipeline stops here.
/// </param>
/// <param name="SpeechDuration">Total speech found, after merging. Logged for the eval corpus.</param>
/// <param name="LeadingTrimmedSamples">
/// How many samples were cut from the front. Added to a segment's offset it gives that segment's
/// position in the original buffer; subtracted, it rebases the segment onto
/// <paramref name="Samples"/>.
/// </param>
/// <param name="Segments">Merged segments, in the original buffer's coordinates.</param>
public sealed record VadResult(
    ReadOnlyMemory<float> Samples,
    bool ContainsSpeech,
    TimeSpan SpeechDuration,
    int LeadingTrimmedSamples,
    IReadOnlyList<VadSegment> Segments);

/// <summary>
/// Trims silence off a captured utterance, and rejects one that holds no speech.
/// </summary>
/// <remarks>
/// Two jobs, deliberately separable. <see cref="Process"/> runs Silero over the buffer to find
/// where the speech is; <see cref="Trim"/> turns those offsets into a trimmed buffer and a
/// verdict. The second is pure arithmetic and is where every rule that can clip a user's first
/// word lives, so it is testable on a machine that has never downloaded a model.
/// <para>
/// Not thread-safe: <see cref="VoiceActivityDetector"/> is a stateful native object and one
/// dictation runs at a time.
/// </para>
/// </remarks>
public sealed class SileroVadGate : IDisposable
{
    /// <summary>
    /// Silero's per-segment ceiling, and what sizes the detector's internal ring.
    /// </summary>
    /// <remarks>
    /// A monologue longer than this is force-split into adjacent segments, which
    /// <see cref="Trim"/> merges straight back together -- so the only effect is that the native
    /// ring stays at a few megabytes instead of the twenty a five-minute ceiling would need.
    /// </remarks>
    private static readonly TimeSpan MaxSegment = TimeSpan.FromSeconds(30);

    private readonly VadOptions _options;
    private readonly VoiceActivityDetector _detector;
    private readonly float[] _window;
    private bool _disposed;

    /// <exception cref="VadModelMissingException">The model file is absent or unloadable.</exception>
    public SileroVadGate(string modelPath, VadOptions? options = null)
    {
        _options = options ?? new VadOptions();

        if (!File.Exists(modelPath))
        {
            throw new VadModelMissingException(modelPath);
        }

        var config = new VadModelConfig
        {
            SampleRate = AudioFormat.SampleRate,
            NumThreads = _options.NumThreads,
            Provider = "cpu",
            Debug = 0,
        };
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = _options.Threshold;
        config.SileroVad.MinSpeechDuration = (float)_options.MinSpeech.TotalSeconds;
        config.SileroVad.MinSilenceDuration = (float)_options.MinSilence.TotalSeconds;
        config.SileroVad.WindowSize = _options.WindowSize;
        config.SileroVad.MaxSpeechDuration = (float)MaxSegment.TotalSeconds;

        try
        {
            _detector = new VoiceActivityDetector(
                config,
                bufferSizeInSeconds: (float)(MaxSegment + TimeSpan.FromSeconds(5)).TotalSeconds);
        }
        catch (Exception ex) when (ex is not VadModelMissingException)
        {
            // A present-but-corrupt file is the same outcome for the user as a missing one, and
            // must fail just as loudly rather than degrading into untrimmed audio.
            throw new VadModelMissingException(modelPath, ex);
        }

        _window = new float[_options.WindowSize];
    }

    /// <summary>Finds the speech in a captured buffer and trims to it.</summary>
    public VadResult Process(ReadOnlyMemory<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Every dictation starts from a clean detector; leaking state between utterances would
        // report the previous one's segments against this one's buffer.
        _detector.Clear();
        _detector.Reset();

        // Fed one window at a time rather than as a single array: sherpa pushes whatever it is
        // handed into a fixed-size native ring before processing any of it, so a five-minute
        // buffer in one call would overrun a ring sized for one utterance.
        var source = samples.Span;
        for (var offset = 0; offset < source.Length; offset += _window.Length)
        {
            var take = Math.Min(_window.Length, source.Length - offset);
            if (take < _window.Length)
            {
                // Zero-pad the final partial window. The model then sees the whole capture, and
                // what it is padded with is the silence that really follows it.
                Array.Clear(_window);
            }

            source.Slice(offset, take).CopyTo(_window);
            _detector.AcceptWaveform(_window);
        }

        _detector.Flush();

        var segments = new List<VadSegment>();
        while (!_detector.IsEmpty())
        {
            var segment = _detector.Front();
            segments.Add(new VadSegment(segment.Start, segment.Samples.Length));
            _detector.Pop();
        }

        return Trim(samples, segments, _options);
    }

    /// <summary>
    /// Turns detected speech offsets into a trimmed buffer and a verdict.
    /// </summary>
    /// <remarks>
    /// Public, and static, so the arithmetic can be tested without the ONNX file -- the plan
    /// puts the VAD model behind a download that Phase 1 owns, and these rules must not go
    /// unverified on a machine that has not run it yet.
    /// </remarks>
    public static VadResult Trim(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<VadSegment> segments,
        VadOptions options)
    {
        var merged = Merge(segments, samples.Length, AudioFormat.SamplesFor(options.MergeGap));

        var speechSamples = 0;
        foreach (var segment in merged)
        {
            speechSamples += segment.SampleCount;
        }

        var speechDuration = AudioFormat.DurationOf(speechSamples);
        if (merged.Count == 0 || speechDuration < options.MinimumUtterance)
        {
            return new VadResult(
                ReadOnlyMemory<float>.Empty,
                ContainsSpeech: false,
                speechDuration,
                LeadingTrimmedSamples: 0,
                merged);
        }

        // Only the ends are trimmed. Interior silence is where the full stops go: cutting a
        // one-second pause between two sentences would splice them into one run-on phrase.
        //
        // The leading pad is the larger of the two, because the detector reports a start that is
        // already one to three 32 ms frames late -- see the remarks on VadOptions. The trailing
        // side needs only enough to keep the last phoneme's tail.
        var leading = AudioFormat.SamplesFor(
            options.LeadingPadding == default ? options.Padding : options.LeadingPadding);
        var trailing = AudioFormat.SamplesFor(options.Padding);
        var start = Math.Max(0, merged[0].StartSample - leading);
        var end = Math.Min(samples.Length, merged[^1].EndSample + trailing);

        return new VadResult(
            samples[start..end],
            ContainsSpeech: true,
            speechDuration,
            LeadingTrimmedSamples: start,
            merged);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detector.Dispose();
    }

    private static List<VadSegment> Merge(IReadOnlyList<VadSegment> segments, int bufferLength, int mergeGap)
    {
        var merged = new List<VadSegment>(segments.Count);

        foreach (var raw in segments)
        {
            // sherpa reports a start plus a sample count, and a flush at the very end of a
            // capture can round past the buffer it was handed.
            var start = Math.Clamp(raw.StartSample, 0, bufferLength);
            var end = Math.Clamp(raw.EndSample, start, bufferLength);
            if (end == start)
            {
                continue;
            }

            if (merged.Count > 0 && start - merged[^1].EndSample <= mergeGap)
            {
                var previous = merged[^1];
                merged[^1] = new VadSegment(previous.StartSample, Math.Max(previous.EndSample, end) - previous.StartSample);
                continue;
            }

            merged.Add(new VadSegment(start, end - start));
        }

        return merged;
    }
}
