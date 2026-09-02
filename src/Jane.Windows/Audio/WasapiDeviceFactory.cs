using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jane.Windows.Audio;

/// <summary>
/// The one place in Jane that touches a real microphone.
/// </summary>
/// <remarks>
/// Shared mode, never exclusive: exclusive mode takes the endpoint away from Discord, from the
/// browser and from anything else the user is mid-call on, which is not a trade a dictation tool
/// gets to make. Communications mode is asked for so Windows applies the same endpoint effects a
/// voice-chat app would get.
/// <para>
/// With no explicit device chosen, NAudio's automatic stream routing follows the default input,
/// so swapping headsets re-routes without a stop at all. It is mutually exclusive with naming a
/// device, and rightly so: a user who picked a specific microphone should not be silently moved
/// off it. <see cref="WasapiCapture"/>'s reconnect loop covers the case routing cannot -- an
/// endpoint that is simply unplugged.
/// </para>
/// </remarks>
public sealed class WasapiDeviceFactory : ICaptureDeviceFactory
{
    private const int BufferMilliseconds = 20;

    public async Task<ICaptureStream> OpenAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var (device, name) = ResolveDevice(deviceId);

        try
        {
            var recorder = await BuildAsync(device, requestJaneFormat: true, cancellationToken).ConfigureAwait(false);
            return new WasapiCaptureStream(device, recorder, name);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // The endpoint refused 16 kHz mono. Fall back to its own mix format and resample in
            // process; DeviceSampleConverter exists for exactly this branch.
            try
            {
                var recorder = await BuildAsync(device, requestJaneFormat: false, cancellationToken).ConfigureAwait(false);
                return new WasapiCaptureStream(device, recorder, name);
            }
            catch (Exception fallback) when (fallback is COMException or InvalidOperationException or NotSupportedException)
            {
                device?.Dispose();
                throw new AudioDeviceException(
                    AudioDeviceFailure.OpenFailed,
                    $"Could not open '{name}' for capture: {fallback.Message}",
                    fallback);
            }
        }
    }

    private static Task<WasapiRecorder> BuildAsync(MMDevice? device, bool requestJaneFormat, CancellationToken cancellationToken)
    {
        var builder = new WasapiRecorderBuilder()
            .WithSharedMode()

            // Event sync ties the callback cadence to the driver rather than to a polling timer,
            // which is what keeps idle CPU near zero while the device stays open.
            .WithEventSync()
            .WithCommunicationsMode()
            .WithBufferLength(BufferMilliseconds);

        if (requestJaneFormat)
        {
            // Shared mode lets the client name its own format and has Windows convert. Its
            // resampler is better than anything Jane would ship, and it removes the whole
            // 48 kHz-stereo fold from the callback path.
            builder = builder.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels));
        }

        builder = device is null
            ? builder.WithDefaultDeviceStreamRouting()
            : builder.WithDevice(device);

        // BuildAsync, not Build: stream routing is activated off-thread, and NAudio refuses a
        // synchronous build once it has been asked for.
        return builder.BuildAsync().WaitAsync(cancellationToken);
    }

    /// <returns>
    /// A null device means "follow whatever Windows calls the default", which is mutually
    /// exclusive with naming one. The endpoint is still enumerated -- and immediately released --
    /// so that "there is no microphone at all" fails with a clear verdict and a real name rather
    /// than as an opaque build error.
    /// </returns>
    private static (MMDevice? Device, string Name) ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (deviceId is null)
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var probe))
            {
                throw new AudioDeviceException(
                    AudioDeviceFailure.NoDevice,
                    "Windows reports no default communications input device.");
            }

            var name = probe.FriendlyName;
            probe.Dispose();
            return (null, name);
        }

        try
        {
            var device = enumerator.GetDevice(deviceId);
            return (device, device.FriendlyName);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new AudioDeviceException(
                AudioDeviceFailure.NoDevice,
                $"Capture device '{deviceId}' is not present.",
                ex);
        }
    }

    private sealed class WasapiCaptureStream : ICaptureStream
    {
        private readonly MMDevice? _device;
        private readonly WasapiRecorder _recorder;
        private readonly DeviceSampleConverter _converter;
        private readonly string _fallbackName;
        private byte[] _silence = [];
        private bool _stopExpected;

        public WasapiCaptureStream(MMDevice? device, WasapiRecorder recorder, string fallbackName)
        {
            _device = device;
            _recorder = recorder;
            _fallbackName = fallbackName;

            // Shared mode hands back the device mix format -- typically 48 kHz stereo float --
            // so the fold to 16 kHz mono happens here, before anything downstream sees a sample.
            _converter = new DeviceSampleConverter(_recorder.WaveFormat);
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.RecordingStopped += OnRecordingStopped;
        }

        /// <summary>Read live, so a routed-to endpoint shows its own name in the overlay.</summary>
        public string DeviceName
        {
            get
            {
                var current = _recorder.DeviceFriendlyName;
                return string.IsNullOrWhiteSpace(current) ? _fallbackName : current;
            }
        }

        public event EventHandler<ReadOnlyMemory<float>>? SamplesAvailable;

        public event EventHandler<AudioDeviceException?>? Stopped;

        public void Start() => _recorder.StartRecording();

        public void Dispose()
        {
            _stopExpected = true;
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.RecordingStopped -= OnRecordingStopped;
            _recorder.Dispose();
            _device?.Dispose();
        }

        private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            if (data.IsEmpty)
            {
                return;
            }

            // WASAPI is allowed to hand back a buffer whose contents are meaningless and flag it
            // silent instead of writing zeroes. Passing that through would feed the VAD noise
            // that only exists in uninitialised memory -- so substitute real silence of the same
            // length and let the converter resample it like any other block.
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                if (_silence.Length < data.Length)
                {
                    _silence = new byte[data.Length];
                }

                SamplesAvailable?.Invoke(this, _converter.Convert(_silence.AsSpan(0, data.Length)));
                return;
            }

            SamplesAvailable?.Invoke(this, _converter.Convert(data));
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_stopExpected)
            {
                return;
            }

            // Unplugging a USB mic surfaces here as AUDCLNT_E_DEVICE_INVALIDATED. A stop with no
            // exception is just as fatal -- the endpoint is gone either way -- so both route to
            // the same recoverable failure rather than only the noisy one.
            Stopped?.Invoke(this, new AudioDeviceException(
                AudioDeviceFailure.Lost,
                e.Exception is null
                    ? $"Capture on '{DeviceName}' stopped unexpectedly."
                    : $"Capture on '{DeviceName}' failed: {e.Exception.Message}"));
        }
    }
}
