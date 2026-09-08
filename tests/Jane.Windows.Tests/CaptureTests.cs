using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// Capture behaviour driven through a synthetic device, so every case runs headless and with no
/// microphone attached. The WASAPI-specific code sits behind <see cref="ICaptureDeviceFactory"/>
/// precisely so these rules can be pinned without hardware.
/// </summary>
/// <remarks>
/// Everything here runs under <see cref="MicrophoneActivation.AlwaysOpen"/>, stated explicitly
/// rather than taken from the default. These are the guarantees that depend on the device being
/// held open -- the full pre-roll window, a device opened exactly once, a reconnect loop that
/// resumes after a disconnect -- and they are all still exactly right under that mode. The
/// default moved to <see cref="MicrophoneActivation.WhileDictating"/> for the sake of Bluetooth
/// headsets, and <see cref="MicrophoneActivationTests"/> is where that mode is pinned.
/// </remarks>
public sealed class CaptureTests
{
    private static readonly AudioCaptureOptions HeldOpen = new()
    {
        Activation = MicrophoneActivation.AlwaysOpen,
    };

    private static readonly AudioCaptureOptions FastOptions = HeldOpen with
    {
        PreRoll = TimeSpan.FromMilliseconds(100),
        ReconnectInterval = TimeSpan.FromMilliseconds(10),
    };

    private static float[] Ramp(int start, int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = start + i;
        }

        return samples;
    }

    private static int Samples(int milliseconds) =>
        AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public async Task PreRollRetainsAudioFromThreeHundredMillisecondsBeforeKeyDown()
    {
        // The plan's headline capture guarantee, end to end: the samples the device produced
        // 300 ms before Arm() must still be in the buffer Stop() hands back.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, HeldOpen);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        devices.Current!.Emit(Ramp(0, AudioFormat.SampleRate * 2)); // two seconds before the key press
        capture.Arm();
        devices.Current.Emit(Ramp(AudioFormat.SampleRate * 2, Samples(200)));

        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(Samples(1000), captured.PreRollSamples);
        var span = captured.Samples.Span;
        // The sample the device produced 300 ms before the key went down.
        Assert.Equal((AudioFormat.SampleRate * 2) - Samples(300), span[captured.PreRollSamples - Samples(300)]);
        // ...and the first sample recorded after it.
        Assert.Equal(AudioFormat.SampleRate * 2, span[captured.PreRollSamples]);
    }

    [Fact]
    public async Task DeviceIsOpenedOnceAtStartupAndArmingNeverReopensIt()
    {
        // Opening on key-down would put device-open latency on the path the user feels most.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        capture.Arm();
        stopwatch.Stop();

        Assert.Equal(1, devices.OpenCount);
        Assert.True(capture.State.IsOpen);
        Assert.True(capture.State.IsCapturing);
        Assert.Equal("Fake Microphone", capture.State.DeviceName);
        // Aqua publishes a <50 ms key-down-to-armed bar; arming is a buffer copy, nothing more.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"Arm took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ToggleModeAutoStopsAtMaxDuration()
    {
        // Toggle mode is the only mode where a capture can plausibly run for minutes -- a
        // forgotten toggle would otherwise grow the buffer until the machine gave out. The
        // audio up to the cap is kept, because the plan says this case proceeds through the
        // pipeline rather than being discarded.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions with
        {
            MaxCaptureDuration = TimeSpan.FromMilliseconds(500),
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        var autoStops = new List<CaptureStopReason>();
        capture.AutoStopped += (_, reason) => autoStops.Add(reason);

        capture.Arm();
        for (var chunk = 0; chunk < 8; chunk++)
        {
            devices.Current!.Emit(new float[Samples(150)]);
        }

        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal([CaptureStopReason.MaxDurationReached], autoStops);
        Assert.Equal(CaptureStopReason.MaxDurationReached, captured.StopReason);
        Assert.Equal(Samples(500), captured.Samples.Length - captured.PreRollSamples);
        Assert.False(capture.State.IsCapturing);
    }

    [Fact]
    public async Task MicDisconnectSurfacesATypedErrorStateAndReconnectResumes()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        var reopened = new TaskCompletionSource();
        capture.StateChanged += (_, state) =>
        {
            if (state is { IsOpen: true, Error: null } && devices.OpenCount > 1)
            {
                reopened.TrySetResult();
            }
        };

        capture.Arm();
        devices.Current!.Emit(new float[Samples(200)]);
        devices.Current.Fault(new AudioDeviceException(AudioDeviceFailure.Lost, "USB microphone removed."));

        Assert.False(capture.State.IsOpen);
        Assert.False(capture.State.IsCapturing);
        Assert.Contains("USB microphone removed.", capture.State.Error);
        Assert.Equal(CaptureStopReason.DeviceLost, capture.Stop(CaptureStopReason.Released).StopReason);

        // The reconnect loop runs on its own, so the app recovers without a restart.
        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(capture.State.IsOpen);
        Assert.Null(capture.State.Error);

        capture.Arm();
        devices.Current!.Emit(Ramp(0, Samples(200)));
        Assert.Equal(Samples(200), capture.Stop(CaptureStopReason.Released).Samples.Length);
    }

    [Fact]
    public async Task PreRollFromALostDeviceNeverLeadsACaptureFromItsReplacement()
    {
        // Two different microphones at two different gains spliced into one utterance would be
        // a strange artefact to debug, so the ring is dropped when the device goes away.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        devices.Current!.Emit(Ramp(1000, Samples(100)));

        devices.Current.Fault(new AudioDeviceException(AudioDeviceFailure.Lost, "gone"));
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        capture.Arm();

        Assert.Equal(0, capture.Stop(CaptureStopReason.Released).PreRollSamples);
    }

    [Fact]
    public async Task OpeningWithNoDevicePresentRaisesATypedError()
    {
        var devices = new FakeCaptureDeviceFactory
        {
            OpenFailure = new AudioDeviceException(AudioDeviceFailure.NoDevice, "No capture endpoint."),
        };
        await using var capture = new WasapiCapture(devices, FastOptions with { AutoReconnect = false });

        var error = await Assert.ThrowsAsync<AudioDeviceException>(
            () => capture.OpenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AudioDeviceFailure.NoDevice, error.Failure);
        Assert.False(capture.State.IsOpen);
        Assert.Contains("No capture endpoint.", capture.State.Error);
    }

    [Fact]
    public async Task StoppingWithoutArmingYieldsAnEmptyBufferRatherThanThrowing()
    {
        // A hotkey release with no matching press -- the key was already down when Jane started
        // -- must be a no-op, not an exception on a background thread.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        devices.Current!.Emit(new float[Samples(100)]);

        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(0, captured.Samples.Length);
        Assert.Equal(0, captured.PreRollSamples);
    }

    [Fact]
    public async Task ACancelledHoldKeepsItsReasonEvenAfterTheMaxDurationCapFired()
    {
        // Esc means "forget it" whatever else happened; the auto-stop reason must not overwrite
        // a deliberate cancel, or Phase 5 would run the pipeline on a discarded buffer.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions with
        {
            MaxCaptureDuration = TimeSpan.FromMilliseconds(100),
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        devices.Current!.Emit(new float[Samples(300)]);

        Assert.Equal(CaptureStopReason.Cancelled, capture.Stop(CaptureStopReason.Cancelled).StopReason);
    }

    [Fact]
    public async Task SamplesArrivingWhileIdleFillThePreRollButAreNotRetainedAsACapture()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        devices.Current!.Emit(Ramp(0, Samples(400)));

        Assert.False(capture.State.IsCapturing);
        capture.Arm();
        var captured = capture.Stop(CaptureStopReason.Released);

        // Only the configured 100 ms window survives, not the whole 400 ms that was emitted.
        Assert.Equal(Samples(100), captured.Samples.Length);
        Assert.Equal(Samples(100), captured.PreRollSamples);
    }

    [Fact]
    public async Task DisposeStopsTheDeviceSoTheMicInUseIndicatorGoesAway()
    {
        var devices = new FakeCaptureDeviceFactory();
        var capture = new WasapiCapture(devices, FastOptions);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        var stream = devices.Current!;

        await capture.DisposeAsync();

        Assert.True(stream.IsDisposed);
        Assert.False(capture.State.IsOpen);
    }

    [Fact]
    public void DeviceSampleConverterFoldsFortyEightKilohertzStereoToSixteenKilohertzMono()
    {
        // Shared-mode WASAPI hands back the device mix format -- 48 kHz stereo float here --
        // and nothing downstream of this point resamples, so the fold has to happen at the edge.
        var converter = new DeviceSampleConverter(sampleRate: 48_000, channels: 2);
        var frames = 48_000; // one second
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[i * 2] = 0.5f;
            interleaved[(i * 2) + 1] = -0.1f;
        }

        var mono = converter.Convert(MemoryMarshal.AsBytes(interleaved.AsSpan()));

        // WDL's resampler has a short filter delay, so allow a couple of milliseconds of slack.
        Assert.InRange(mono.Length, AudioFormat.SampleRate - 200, AudioFormat.SampleRate);
        // Left and right average to 0.2; a converter that dropped a channel would read 0.5.
        Assert.All(mono[100..].ToArray(), sample => Assert.InRange(sample, 0.15f, 0.25f));
    }

    [Fact]
    public void DeviceSampleConverterKeepsConvertingAcrossManyCallbacks()
    {
        // Regression: WDL's resampler commits the input it prepared even when it cannot fill the
        // request, so asking for a round 4096 frames when a 20 ms callback only carries 160
        // silently discarded every callback after the first. On a real microphone that showed up
        // as ten milliseconds of audio followed by nothing at all.
        var converter = new DeviceSampleConverter(sampleRate: 48_000, channels: 2);
        var callback = new float[480 * 2]; // 10 ms of 48 kHz stereo, one WASAPI packet
        Array.Fill(callback, 0.25f);

        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            total += converter.Convert(MemoryMarshal.AsBytes(callback.AsSpan())).Length;
        }

        // 100 callbacks of 10 ms is one second, and nothing may be lost along the way.
        Assert.Equal(AudioFormat.SampleRate, total);
    }

    [Fact]
    public void DeviceSampleConverterPassesThroughAlreadyCorrectFormats()
    {
        var converter = new DeviceSampleConverter(AudioFormat.SampleRate, AudioFormat.Channels);
        var input = Ramp(0, 160);

        var output = converter.Convert(MemoryMarshal.AsBytes(input.AsSpan()));

        Assert.Equal(input, output.ToArray());
    }
}
