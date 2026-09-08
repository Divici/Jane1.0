using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// A capture device that exists only in memory, so every rule in <see cref="WasapiCapture"/> can
/// be driven headless and with no microphone attached.
/// </summary>
/// <remarks>
/// Shared between <see cref="CaptureTests"/> and <see cref="MicrophoneActivationTests"/>. It
/// records what it was asked for -- how many times, and for which device id -- because "the
/// device was opened once, on demand, and for the endpoint the user chose" is the whole subject
/// of the activation tests and is invisible from the samples alone.
/// </remarks>
internal sealed class FakeCaptureDeviceFactory : ICaptureDeviceFactory
{
    private readonly List<string?> _requested = [];

    public int OpenCount { get; private set; }

    public AudioDeviceException? OpenFailure { get; init; }

    /// <summary>Blocks each open until released. Left null, opens complete immediately.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>
    /// How long opening the device takes, standing in for endpoint activation and format
    /// negotiation. Real hardware is 100-400 ms, and a Bluetooth headset can be worse.
    /// </summary>
    public TimeSpan OpenDelay { get; init; }

    /// <summary>
    /// How long activating the endpoint takes. Real drivers are single-digit milliseconds; a
    /// device where it is not is the case the latency probe exists to catch.
    /// </summary>
    public TimeSpan StartDelay { get; init; }

    public FakeCaptureStream? Current { get; private set; }

    /// <summary>Every device id passed to <see cref="OpenAsync"/>, in order.</summary>
    public IReadOnlyList<string?> Requested => _requested;

    public async Task<ICaptureStream> OpenAsync(string? deviceId, CancellationToken cancellationToken)
    {
        OpenCount++;
        _requested.Add(deviceId);

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (OpenDelay > TimeSpan.Zero)
        {
            await Task.Delay(OpenDelay, cancellationToken).ConfigureAwait(false);
        }

        if (OpenFailure is not null)
        {
            throw new AudioDeviceException(OpenFailure.Failure, OpenFailure.Message);
        }

        Current = new FakeCaptureStream { StartDelay = StartDelay };
        return Current;
    }
}

internal sealed class FakeCaptureStream : ICaptureStream
{
    public string DeviceName => "Fake Microphone";

    public bool IsDisposed { get; private set; }

    /// <summary>The audio client exists and is initialised. Silent to the rest of the system.</summary>
    public bool IsPrepared { get; private set; }

    /// <summary>The endpoint is activated. This is the state a Bluetooth headset reacts to.</summary>
    public bool IsRunning { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    /// <summary>Stands in for a driver that is slow to activate its endpoint.</summary>
    public TimeSpan StartDelay { get; init; }

    public event EventHandler<ReadOnlyMemory<float>>? SamplesAvailable;

    public event EventHandler<AudioDeviceException?>? Stopped;

    public void Prepare() => IsPrepared = true;

    public void Start()
    {
        // Starting without preparing is a bug in the caller, not something a device tolerates
        // quietly: on real hardware it is an uninitialised client and an E_FAIL.
        Assert.True(IsPrepared, "Start was called on a stream that was never prepared.");

        if (IsRunning)
        {
            return;
        }

        if (StartDelay > TimeSpan.Zero)
        {
            Thread.Sleep(StartDelay);
        }

        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        StopCount++;
    }

    /// <summary>
    /// Delivers samples, and fails the test if the stream is not running.
    /// </summary>
    /// <remarks>
    /// A prepared-but-stopped client that produced audio would be an always-open microphone
    /// wearing a different name, so the fake refuses to model one.
    /// </remarks>
    public void Emit(ReadOnlyMemory<float> samples)
    {
        Assert.True(IsRunning, "Samples cannot arrive from a stream that is not running.");
        SamplesAvailable?.Invoke(this, samples);
    }

    public void Fault(AudioDeviceException error) => Stopped?.Invoke(this, error);

    public void Dispose()
    {
        IsRunning = false;
        IsDisposed = true;
    }
}
