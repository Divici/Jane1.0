namespace Jane.Core.Abstractions;

/// <summary>The sample format every stage of Jane's pipeline speaks.</summary>
public static class AudioFormat
{
    /// <summary>Parakeet and Silero VAD both want 16 kHz mono; nothing downstream resamples.</summary>
    public const int SampleRate = 16_000;

    public const int Channels = 1;

    public static TimeSpan DurationOf(int sampleCount) =>
        TimeSpan.FromSeconds(sampleCount / (double)SampleRate);

    public static int SamplesFor(TimeSpan duration) =>
        (int)Math.Round(duration.TotalSeconds * SampleRate);
}

/// <summary>Why a capture ended. Drives which pipeline state the orchestrator lands in.</summary>
public enum CaptureStopReason
{
    /// <summary>The hotkey was released, or toggle mode was stopped by the user.</summary>
    Released,

    /// <summary>The hold was shorter than the configured minimum. Discard silently.</summary>
    TooShort,

    /// <summary>Toggle mode hit its maximum duration. Proceed through the pipeline anyway.</summary>
    MaxDurationReached,

    /// <summary>Esc, or a second hotkey press in toggle mode meaning "forget it".</summary>
    Cancelled,

    /// <summary>The device went away mid-capture.</summary>
    DeviceLost,
}

/// <param name="Samples">16 kHz mono float PCM in [-1, 1], including any pre-roll.</param>
/// <param name="PreRollSamples">
/// How many leading samples came from the ring buffer rather than from after key-down. The VAD
/// needs this to distinguish "silence before the user started" from "silence they meant".
/// </param>
public sealed record CapturedAudio(
    ReadOnlyMemory<float> Samples,
    int PreRollSamples,
    CaptureStopReason StopReason,
    DateTimeOffset StartedAt)
{
    public TimeSpan Duration => AudioFormat.DurationOf(Samples.Length);
}

/// <param name="IsCapturing">True between arm and stop. The overlay's "listening" state mirrors this.</param>
public sealed record AudioSourceState(bool IsOpen, bool IsCapturing, string? DeviceName, string? Error);

/// <summary>
/// A microphone, opened once at app start and held open.
/// </summary>
/// <remarks>
/// Opening on key-down would put device-open latency -- tens to hundreds of milliseconds -- on
/// the path the user feels most, and would clip the first word. The cost is a permanent
/// mic-in-use indicator, which onboarding discloses.
/// </remarks>
public interface IAudioSource : IAsyncDisposable
{
    AudioSourceState State { get; }

    event EventHandler<AudioSourceState>? StateChanged;

    /// <summary>Opens the device and starts filling the pre-roll ring. Idempotent.</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Begins retaining samples, back-dated by the pre-roll window. Free -- no model work starts
    /// here; that waits until the VAD confirms real speech.
    /// </summary>
    void Arm();

    /// <summary>Ends the capture and returns everything retained since <see cref="Arm"/>.</summary>
    CapturedAudio Stop(CaptureStopReason reason);
}
