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

        if (OpenFailure is not null)
        {
            throw new AudioDeviceException(OpenFailure.Failure, OpenFailure.Message);
        }

        Current = new FakeCaptureStream();
        return Current;
    }
}

internal sealed class FakeCaptureStream : ICaptureStream
{
    public string DeviceName => "Fake Microphone";

    public bool IsDisposed { get; private set; }

    public event EventHandler<ReadOnlyMemory<float>>? SamplesAvailable;

    public event EventHandler<AudioDeviceException?>? Stopped;

    public void Start()
    {
    }

    public void Emit(ReadOnlyMemory<float> samples) => SamplesAvailable?.Invoke(this, samples);

    public void Fault(AudioDeviceException error) => Stopped?.Invoke(this, error);

    public void Dispose() => IsDisposed = true;
}
