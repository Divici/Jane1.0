using Jane.Core.Abstractions;

namespace Jane.Windows.Audio;

/// <summary>Why a capture device could not be used. The distinction drives what the overlay says.</summary>
public enum AudioDeviceFailure
{
    /// <summary>Windows reports no active capture endpoint at all.</summary>
    NoDevice,

    /// <summary>The endpoint exists but would not open -- exclusive-mode owner, driver fault.</summary>
    OpenFailed,

    /// <summary>The device went away while it was open. Recoverable: Jane reconnects.</summary>
    Lost,
}

/// <summary>
/// A typed audio failure, so callers branch on <see cref="Failure"/> rather than on message text.
/// </summary>
/// <remarks>
/// <see cref="AudioSourceState"/> carries only a string, because it crosses into
/// <c>Jane.Core</c>, which owns no Win32 concepts. This exception is where the *kind* of failure
/// survives, and it is what <see cref="IAudioSource.OpenAsync"/> throws.
/// </remarks>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException(AudioDeviceFailure failure, string message)
        : base(message) => Failure = failure;

    public AudioDeviceException(AudioDeviceFailure failure, string message, Exception innerException)
        : base(message, innerException) => Failure = failure;

    public AudioDeviceFailure Failure { get; }
}

/// <summary>
/// An open microphone, already folded down to Jane's 16 kHz mono float format.
/// </summary>
/// <remarks>
/// This is the seam that keeps every capture rule testable without hardware: the WASAPI-specific
/// half lives in <see cref="WasapiDeviceFactory"/>, and <see cref="WasapiCapture"/> -- which owns
/// the pre-roll, the arming and the duration cap -- talks only to this interface.
/// </remarks>
public interface ICaptureStream : IDisposable
{
    /// <summary>Friendly endpoint name, for the overlay and for <see cref="AudioSourceState"/>.</summary>
    string DeviceName { get; }

    /// <summary>
    /// Raised on the device's own callback thread. The memory is only valid for the duration of
    /// the handler -- implementations reuse their conversion scratch buffer, so a handler that
    /// wants to keep the samples must copy them.
    /// </summary>
    event EventHandler<ReadOnlyMemory<float>>? SamplesAvailable;

    /// <summary>Capture ended. A non-null argument means the device faulted or went away.</summary>
    event EventHandler<AudioDeviceException?>? Stopped;

    /// <summary>
    /// Readies the audio client without activating the endpoint. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the expensive half of opening a microphone -- activating the endpoint's COM object,
    /// negotiating a format, allocating the engine buffer -- and on a Bluetooth headset it is
    /// hundreds of milliseconds. It is also the silent half: no samples flow, the capture
    /// indicator stays dark, and the headset stays in its stereo profile, because Windows reacts
    /// to a stream being <em>started</em>, not to a client existing.
    /// </para>
    /// <para>
    /// Separating it from <see cref="Start"/> is what lets the microphone stay off until the
    /// hotkey goes down and still capture the first word: the cost is paid at launch, and key-down
    /// pays only for <see cref="Start"/>.
    /// </para>
    /// </remarks>
    void Prepare();

    /// <summary>
    /// Activates the endpoint. Samples begin arriving. Callable <b>once</b> per stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stream is single-use, and that is a property of the hardware rather than a simplification.
    /// NAudio's <c>StopRecording</c> leaves the underlying <c>IAudioClient</c> initialised while
    /// its <c>StartRecording</c> initialises unconditionally, so a second start fails with
    /// <c>AUDCLNT_E_ALREADY_INITIALIZED</c>. Measured on real hardware, both with default-device
    /// routing and with an explicit endpoint id.
    /// </para>
    /// <para>
    /// There is deliberately no <c>Stop</c> alongside this. One existed, promising to deactivate
    /// the endpoint while leaving the client reusable; no implementation could honour the second
    /// half, and the result was that every dictation after the first idle release failed with
    /// "No microphone". Deactivating means <see cref="IDisposable.Dispose"/> and building another,
    /// which is what <c>WasapiCapture</c> does at the end of its idle window -- off the key-down
    /// path, where the cost is invisible.
    /// </para>
    /// </remarks>
    void Start();
}

/// <summary>Opens capture endpoints. One real implementation, one fake in the tests.</summary>
public interface ICaptureDeviceFactory
{
    /// <param name="deviceId">A WASAPI endpoint id, or null for the default communications device.</param>
    /// <exception cref="AudioDeviceException">No usable endpoint.</exception>
    /// <remarks>
    /// Asynchronous because NAudio activates automatic stream routing off-thread and refuses a
    /// synchronous build when it is asked for. This runs once at app start, never on key-down.
    /// </remarks>
    Task<ICaptureStream> OpenAsync(string? deviceId, CancellationToken cancellationToken);
}

/// <param name="PreRoll">
/// How much audio from before key-down is retained. One second by default -- see
/// <see cref="PreRollBuffer"/> for why the window exists at all.
/// </param>
/// <param name="MaxCaptureDuration">
/// Hard cap on a single capture. Toggle mode makes it possible to leave dictation running by
/// accident, and an uncapped buffer would grow until the machine gave out; at the cap the audio
/// so far is kept and the pipeline proceeds.
/// </param>
/// <param name="ReconnectInterval">How often to retry a lost or unavailable device.</param>
/// <param name="AutoReconnect">
/// Whether to retry at all. Off in tests that assert the failure state itself.
/// </param>
/// <param name="DeviceId">Explicit endpoint, or null for the default communications device.</param>
/// <param name="Activation">Whether the device is held open or opened per dictation.</param>
/// <param name="IdleRelease">
/// How long an on-demand device stays open after a dictation ends. Zero releases it immediately,
/// which makes every dictation a cold open.
/// </param>
public sealed record AudioCaptureOptions(
    TimeSpan PreRoll,
    TimeSpan MaxCaptureDuration,
    TimeSpan ReconnectInterval,
    bool AutoReconnect,
    string? DeviceId,
    MicrophoneActivation Activation = MicrophoneActivation.WhileDictating,
    TimeSpan IdleRelease = default)
{
    public AudioCaptureOptions()
        : this(
            PreRollBuffer.DefaultWindow,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(2),
            AutoReconnect: true,
            DeviceId: null,
            Activation: MicrophoneActivation.WhileDictating,
            IdleRelease: TimeSpan.FromSeconds(8))
    {
    }

    /// <summary>These options with a routing applied, leaving everything else alone.</summary>
    public AudioCaptureOptions With(MicrophoneRouting routing)
    {
        ArgumentNullException.ThrowIfNull(routing);

        return this with
        {
            DeviceId = routing.DeviceId,
            Activation = routing.Activation,
            IdleRelease = routing.IdleRelease,
        };
    }
}
