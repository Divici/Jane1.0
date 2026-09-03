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

/// <summary>When Jane holds a capture stream on the microphone.</summary>
/// <remarks>
/// <para>
/// This is not the free choice it looks like. Windows switches a Bluetooth headset from A2DP to
/// the hands-free profile the moment any process opens a capture stream on it, and the hands-free
/// profile is a narrowband mono call channel -- so an always-open microphone silently degrades
/// every other sound on the machine for as long as Jane is running.
/// </para>
/// <para>
/// The plan originally priced holding the device open as "a permanent mic-in-use indicator". On
/// a wired microphone that is the whole cost and <see cref="AlwaysOpen"/> is the better setting.
/// On Bluetooth it is not, which is why the default changed.
/// </para>
/// </remarks>
public enum MicrophoneActivation
{
    /// <summary>
    /// Default. The device is opened when the hotkey goes down and released a short while after
    /// the dictation ends. Costs the device-open latency on a cold press -- and with it the
    /// pre-roll, which cannot back-date audio that was never captured.
    /// </summary>
    WhileDictating,

    /// <summary>
    /// Opened at startup and held. No open latency, full pre-roll, and a microphone that is in
    /// use for as long as Jane is.
    /// </summary>
    AlwaysOpen,
}

/// <param name="DeviceId">Null means the Windows default communications input.</param>
/// <param name="IdleRelease">
/// How long a <see cref="MicrophoneActivation.WhileDictating"/> device stays open after a
/// dictation ends. Long enough that a run of quick dictations pays the open cost once; short
/// enough that a headset is back in stereo before the user notices it left.
/// </param>
public sealed record MicrophoneRouting(
    string? DeviceId,
    MicrophoneActivation Activation,
    TimeSpan IdleRelease);

/// <summary>
/// A microphone.
/// </summary>
/// <remarks>
/// Whether the device is held open or opened per dictation is
/// <see cref="MicrophoneActivation"/>'s decision, and the difference is invisible from here:
/// <see cref="OpenAsync"/> is always called at startup and <see cref="Arm"/> is always called on
/// key-down, whichever of them actually touches the hardware.
/// </remarks>
public interface IAudioSource : IAsyncDisposable
{
    AudioSourceState State { get; }

    event EventHandler<AudioSourceState>? StateChanged;

    /// <summary>
    /// Readies the microphone. Opens the device under
    /// <see cref="MicrophoneActivation.AlwaysOpen"/> and does nothing under
    /// <see cref="MicrophoneActivation.WhileDictating"/>. Idempotent.
    /// </summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Begins retaining samples, back-dated by whatever pre-roll exists. No model work starts
    /// here; that waits until the VAD confirms real speech.
    /// </summary>
    /// <remarks>
    /// Never blocks. Under <see cref="MicrophoneActivation.WhileDictating"/> this starts a device
    /// open and returns; samples are retained from the moment the device is live.
    /// </remarks>
    void Arm();

    /// <summary>Ends the capture and returns everything retained since <see cref="Arm"/>.</summary>
    CapturedAudio Stop(CaptureStopReason reason);

    /// <summary>Changes which device is used and when it is held open, without a restart.</summary>
    void Reconfigure(MicrophoneRouting routing);
}
