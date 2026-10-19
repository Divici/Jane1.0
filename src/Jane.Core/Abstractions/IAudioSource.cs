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

    /// <summary>
    /// How much of the capture went by before the device delivered any sound at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero when the device was already delivering sound at key-down, which is the whole of the
    /// case for a microphone held open. A positive value is dead air: the stream was started on
    /// key-down and handed back digital silence while it woke up -- measured at 360 to 570 ms on a
    /// Jabra Link 380, and the words spoken in that window were never recorded. Null means no
    /// sound arrived during the capture at all, which is a muted or dead microphone rather than a
    /// quiet user, and is reported as such.
    /// </para>
    /// <para>
    /// Measured in audio delivered rather than on a clock, so it says how much of the buffer is
    /// dead rather than how long a thread took to notice. Defaults to zero so a source that does
    /// not measure it never produces a microphone failure it cannot stand behind.
    /// </para>
    /// </remarks>
    public TimeSpan? StartupLag { get; init; } = TimeSpan.Zero;
}

/// <param name="IsOpen">The endpoint is activated: a capture stream has been started on it.</param>
/// <param name="IsCapturing">True between arm and stop. The overlay's "listening" state mirrors this.</param>
/// <param name="IsLive">
/// The started stream has delivered actual sound. Distinct from <paramref name="IsOpen"/> because
/// a stream that has just been started hands back digital silence while the device wakes -- half
/// a second on a Bluetooth headset -- and a pill that said "Listening" during that window invited
/// the user to speak into a microphone that was not hearing them yet.
/// </param>
public sealed record AudioSourceState(
    bool IsOpen,
    bool IsCapturing,
    string? DeviceName,
    string? Error,
    bool IsLive = false);

/// <summary>When Jane holds a capture stream on the microphone.</summary>
/// <remarks>
/// <para>
/// This is not the free choice it looks like. Windows switches a Bluetooth headset from A2DP to
/// the hands-free profile the moment any process opens a capture stream on it, and the hands-free
/// profile is a narrowband mono call channel -- so an always-open microphone degrades every other
/// sound on the machine for as long as Jane is running. That is why <see cref="WhileDictating"/>
/// was the default for a while.
/// </para>
/// <para>
/// It stopped being the default because of a measurement. On a Jabra Link 380 a started stream
/// returns from its start call in 10 ms and delivers its first buffer at 40 ms -- and every sample
/// is zero until 360 to 570 ms have passed, while the dongle brings the headset's link up. No
/// amount of readying the client in advance shortens that: the dead zone is the headset's, not the
/// host's. Anything the user said in it was never recorded, and the field log showed exactly that:
/// 40 of 49 "no speech" dictations were cold starts, two of them holding a whole short sentence.
/// </para>
/// <para>
/// So the default is <see cref="AlwaysOpen"/>, which has no dead zone at all and a full second of
/// pre-roll at every key press, at the cost of the microphone-in-use indicator and a Bluetooth
/// headset held in its call profile. Somebody who would rather have the headset in stereo between
/// dictations chooses <see cref="WhileDictating"/>, and gets a pill that says Connecting until
/// sound actually arrives rather than one that says Listening while it is not.
/// </para>
/// </remarks>
public enum MicrophoneActivation
{
    /// <summary>
    /// Default. Capture starts when the hotkey goes down and stops a while after the dictation
    /// ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen for Bluetooth headsets: Windows holds a headset in its narrowband call profile for
    /// as long as any application is capturing, and music through it sounds like a phone call
    /// until the stream closes. The device is readied ahead of time -- at launch and again straight
    /// after each idle release, because a capture stream cannot be started twice -- so key-down
    /// pays only for starting the stream. Starting is quick to <em>return</em>; the device then
    /// hands back digital silence until it is actually awake, half a second on a Bluetooth link,
    /// and nothing said before that moment exists to be recorded.
    /// </para>
    /// <para>
    /// The overlay therefore shows Connecting until the first buffer with sound in it arrives, and
    /// the capture records how much dead air there was, so the user is told to wait rather than
    /// invited to speak into a microphone that is not hearing them yet.
    /// </para>
    /// </remarks>
    WhileDictating,

    /// <summary>
    /// Opened at startup and held. Nothing to wake on key-down, a full second of pre-roll behind
    /// every press, and a microphone that is in use -- and a Bluetooth headset that is in its call
    /// profile -- for as long as Jane is running.
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
