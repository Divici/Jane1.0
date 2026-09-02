using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.App.Onboarding;

/// <summary>
/// Opens a microphone and reports its level until it is disposed.
/// </summary>
/// <remarks>
/// Separate from <see cref="Jane.App.Settings.IMicrophoneCatalog"/> because listing devices and
/// opening one are very different risks: enumeration always works, and opening can fail because
/// another application holds the endpoint, because a driver is wedged, or because the user
/// revoked microphone permission. Onboarding has to say which.
/// </remarks>
public interface IMicrophoneCheck
{
    /// <param name="onLevel">Called with a 0-1 level, on the capture thread.</param>
    /// <param name="onFailure">Called once with a sentence a person can act on.</param>
    /// <returns>Dispose to stop capturing and release the device.</returns>
    IDisposable Start(string? deviceId, Action<float> onLevel, Action<string> onFailure);
}

/// <summary>
/// The real check, over the same WASAPI path the dictation pipeline uses.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same <see cref="WasapiDeviceFactory"/> the pipeline opens with, in shared
/// mode, asking for the same 16 kHz mono format. A mic check that used a different code path
/// would answer a question nobody asked -- what matters is whether <em>Jane's</em> capture works,
/// including the endpoint's own format negotiation and the resampling fallback behind it.
/// </para>
/// <para>
/// The level is a peak rather than an RMS. RMS is the better measure of loudness and the worse
/// one for a meter somebody is watching while they say "testing": peak moves the instant they
/// speak, which is the entire feedback this step exists to give.
/// </para>
/// </remarks>
public sealed class WasapiMicrophoneCheck : IMicrophoneCheck
{
    public IDisposable Start(string? deviceId, Action<float> onLevel, Action<string> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onLevel);
        ArgumentNullException.ThrowIfNull(onFailure);

        var session = new Session(onLevel, onFailure);
        session.Open(deviceId);
        return session;
    }

    private sealed class Session(Action<float> onLevel, Action<string> onFailure) : IDisposable
    {
        private ICaptureStream? _stream;
        private bool _disposed;

        public void Open(string? deviceId) => _ = OpenAsync(deviceId);

        private async Task OpenAsync(string? deviceId)
        {
            try
            {
                var stream = await new WasapiDeviceFactory().OpenAsync(deviceId, CancellationToken.None);

                lock (this)
                {
                    if (_disposed)
                    {
                        stream.Dispose();
                        return;
                    }

                    _stream = stream;
                }

                stream.SamplesAvailable += OnSamples;
                stream.Stopped += OnStopped;
                stream.Start();
            }
            catch (AudioDeviceException ex)
            {
                onFailure(Describe(ex));
            }
        }

        private void OnSamples(object? sender, ReadOnlyMemory<float> samples)
        {
            var span = samples.Span;
            var peak = 0f;

            for (var i = 0; i < span.Length; i++)
            {
                var magnitude = Math.Abs(span[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            onLevel(Math.Clamp(peak, 0f, 1f));
        }

        private void OnStopped(object? sender, AudioDeviceException? failure)
        {
            if (failure is not null)
            {
                onFailure(Describe(failure));
            }
        }

        private static string Describe(AudioDeviceException ex) => ex.Failure switch
        {
            AudioDeviceFailure.NoDevice =>
                "Windows reports no active capture device. Plug a microphone in, or enable one under Settings, System, Sound, Input.",
            AudioDeviceFailure.OpenFailed =>
                $"Jane could not open that microphone: {ex.Message} Another application may be holding it in exclusive mode.",
            AudioDeviceFailure.Lost =>
                "The microphone went away while Jane was listening to it. Reconnect it and try again.",
            _ => ex.Message,
        };

        public void Dispose()
        {
            ICaptureStream? stream;

            lock (this)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                stream = _stream;
                _stream = null;
            }

            if (stream is null)
            {
                return;
            }

            stream.SamplesAvailable -= OnSamples;
            stream.Stopped -= OnStopped;
            stream.Dispose();
        }
    }
}
