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
        private bool _disposing;
        private bool _prepared;
        private string? _prepareFailure;
        private int _expectedStops;

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

        /// <summary>
        /// Everything that can be done to ready this device without activating it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The expensive, silent half of readying a microphone has already happened by the time
        /// this object exists: <see cref="WasapiRecorderBuilder.BuildAsync"/> resolves the
        /// endpoint, negotiates the format and activates stream routing, which is why
        /// <see cref="WasapiRecorder.WaveFormat"/> can be read in the constructor. Moving
        /// <see cref="ICaptureDeviceFactory.OpenAsync"/> to startup is what takes that off the
        /// key-down path, and it is the larger half.
        /// </para>
        /// <para>
        /// The remainder -- <c>IAudioClient::Initialize</c> and the capture thread -- is private
        /// to NAudio's <c>StartRecording</c>: <c>InitializeStandard</c> and
        /// <c>InitializeAudioClient</c> are both private instance methods on a class whose
        /// <c>audioClient</c> field is private too, so there is no supported seam and no
        /// protected member to subclass. Reaching in by reflection would put a private-member
        /// lookup on the path between a key press and a microphone, where the failure mode is
        /// "Jane records nothing" -- not a trade worth making for the smaller half. What is left
        /// on key-down is measured by <c>jane doctor</c> rather than asserted here.
        /// </para>
        /// <para>
        /// What this does do is opt the session out of communications ducking, which has to happen
        /// before the session goes active and therefore cannot happen anywhere else. Jane asks for
        /// communications mode so Windows applies echo cancellation and noise suppression to the
        /// capture, and Windows throws in a side-effect nobody asked for: while a communications
        /// session is active it attenuates every other stream on the machine. For a dictation tool
        /// that means music drops each time the user holds the hotkey -- part of the "it messes up
        /// the sound of everything else" report. The processing is worth having; the ducking is not.
        /// </para>
        /// </remarks>
        public void Prepare()
        {
            if (_prepared)
            {
                return;
            }

            _prepared = true;

            try
            {
                using var enumerator = _device is null ? new MMDeviceEnumerator() : null;
                var device = _device;

                if (device is null && enumerator is not null &&
                    !enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out device))
                {
                    return;
                }

                using var owned = _device is null ? device : null;
                device!.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
            {
                // Best effort. A session that would not take the preference still records fine;
                // the only consequence is that Windows keeps turning the music down.
                _prepareFailure = ex.Message;
            }
        }

        /// <summary>Why the ducking opt-out did not take, if it did not. Diagnostics only.</summary>
        public string? PrepareFailure => _prepareFailure;

        public void Start() => _recorder.StartRecording();

        /// <summary>
        /// Deactivates the endpoint, leaving the recorder reusable.
        /// </summary>
        /// <remarks>
        /// This is what the idle-release timer does. The headset returns to stereo and the privacy
        /// indicator goes out, because both follow the capture stream stopping -- while the
        /// endpoint resolution and format negotiation stay done, so the next key press does not
        /// pay for them again.
        /// </remarks>
        public void Stop()
        {
            // Counted rather than flagged: NAudio raises RecordingStopped through a captured
            // synchronisation context, so it can arrive well after this method returns. A flag
            // cleared in a finally would be back to false by then and this deliberate stop would
            // be reported as a lost device.
            Interlocked.Increment(ref _expectedStops);
            _recorder.StopRecording();
        }

        public void Dispose()
        {
            _disposing = true;
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
            if (_disposing)
            {
                return;
            }

            // A stop Jane asked for, that stopped cleanly, is not a fault. One that asked and
            // still came back with an exception is: the endpoint went away during the stop, and
            // pretending otherwise would leave a dead device looking merely idle.
            if (Interlocked.Exchange(ref _expectedStops, 0) > 0 && e.Exception is null)
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
