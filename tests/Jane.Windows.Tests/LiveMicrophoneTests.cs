using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// A started microphone is not a listening microphone, and Jane has to know the difference.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a Jabra Link 380 with a probe that timed every buffer after <c>Start</c>: the call
/// returned in 10 ms, the first buffer arrived at 30 to 40 ms, and every sample in every buffer
/// was exactly zero until 360 to 570 ms had passed. The dongle hands the host a running stream
/// while it renegotiates the link to the headset, and fills it with digital silence meanwhile.
/// </para>
/// <para>
/// <see cref="AudioSourceState.IsOpen"/> went true when the start call returned, the pill flipped
/// to "Listening" on the strength of it, and the user began the sentence half a second before the
/// microphone was hearing anything. The field log shows the result: of 49 dictations that ended in
/// "No speech detected", 40 were cold starts, and the two from the morning this was diagnosed held
/// 1.6 and 2.0 seconds of audio each -- a whole short sentence, lost. So "live" is a separate fact
/// from "open", it is earned by the first buffer that carries sound, and the capture records how
/// much dead air preceded it.
/// </para>
/// </remarks>
public sealed class LiveMicrophoneTests
{
    private static readonly AudioCaptureOptions OnDemand = new()
    {
        PreRoll = TimeSpan.FromMilliseconds(500),
        ReconnectInterval = TimeSpan.FromMilliseconds(10),
        Activation = MicrophoneActivation.WhileDictating,
        IdleRelease = TimeSpan.FromMilliseconds(60),
    };

    private static readonly AudioCaptureOptions HeldOpen = OnDemand with
    {
        Activation = MicrophoneActivation.AlwaysOpen,
    };

    private static int Samples(int milliseconds) => AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public async Task AStartedStreamIsNotLiveUntilItDeliversSound()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;

        // Started, so "open" -- and the hardware is still waking up, so not live.
        Assert.True(capture.State.IsOpen);
        Assert.False(capture.State.IsLive);

        devices.Current!.Emit(new float[Samples(300)]);
        Assert.False(capture.State.IsLive);

        devices.Current.Emit(FakeCaptureStream.Tone(Samples(20)));
        Assert.True(capture.State.IsLive);
    }

    [Fact]
    public async Task GoingLiveIsPublishedExactlyOnce()
    {
        // The overlay flips from Connecting to Listening on this event. Once is enough, and a
        // publish per 20 ms buffer would be fifty redraws a second for nothing.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        var wentLive = 0;
        var previouslyLive = false;
        capture.StateChanged += (_, state) =>
        {
            if (state.IsLive && !previouslyLive)
            {
                wentLive++;
            }

            previouslyLive = state.IsLive;
        };

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(new float[Samples(100)]);
        devices.Current.Emit(FakeCaptureStream.Tone(Samples(20)));
        devices.Current.Emit(FakeCaptureStream.Tone(Samples(20)));
        devices.Current.Emit(FakeCaptureStream.Tone(Samples(20)));

        Assert.Equal(1, wentLive);
    }

    [Fact]
    public async Task ReleasingTheStreamTakesLiveAwayWithIt()
    {
        // A fresh stream after the idle window is a fresh dead zone, and must not inherit the
        // previous one's verdict.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(FakeCaptureStream.Tone(Samples(20)));
        Assert.True(capture.State.IsLive);

        _ = capture.Stop(CaptureStopReason.Released);
        await WaitUntil(() => devices.OpenCount == 2);
        await capture.Armed;

        Assert.False(capture.State.IsLive);
    }

    [Fact]
    public async Task ACaptureRecordsHowMuchDeadAirCameBeforeTheFirstSound()
    {
        // The number the field log was missing. "audio=1630ms preroll=0ms" could not say whether
        // the user spoke for 1.6 seconds or the microphone slept through most of it.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(new float[Samples(400)]);
        devices.Current.Emit(FakeCaptureStream.Tone(Samples(600)));

        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(TimeSpan.FromMilliseconds(400), audio.StartupLag);
        Assert.Equal(Samples(1000), audio.Samples.Length);
    }

    [Fact]
    public async Task ACaptureThatNeverHeardSoundSaysSoRatherThanClaimingZeroLag()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(new float[Samples(1500)]);

        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Null(audio.StartupLag);
    }

    [Fact]
    public async Task ADeviceAlreadyDeliveringSoundHasNoStartupLagAtAll()
    {
        // The held-open case, and the reason it is now the default: there is nothing to wake.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, HeldOpen);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        devices.Current!.Emit(FakeCaptureStream.Tone(Samples(300)));
        Assert.True(capture.State.IsLive);

        capture.Arm();
        devices.Current.Emit(new float[Samples(200)]);
        devices.Current.Emit(FakeCaptureStream.Tone(Samples(200)));

        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(TimeSpan.Zero, audio.StartupLag);
        Assert.Equal(Samples(300), audio.PreRollSamples);
    }

    [Fact]
    public async Task ALostDeviceIsNotLive()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, HeldOpen with { AutoReconnect = false });
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        devices.Current!.Emit(FakeCaptureStream.Tone(Samples(20)));

        devices.Current.Fault(new AudioDeviceException(AudioDeviceFailure.Lost, "USB microphone removed."));

        Assert.False(capture.State.IsLive);
    }

    [Fact]
    public void TheShippedDefaultStartsTheMicrophoneOnDemand()
    {
        // Holding the device open would remove the wake-up dead zone, but it also holds a
        // Bluetooth headset in its narrowband call profile for as long as Jane runs, and music
        // quality won. The dead zone is made visible instead: the pill says Connecting until the
        // first buffer with sound in it, so the user knows when the microphone is really hearing.
        var options = new AudioCaptureOptions();

        Assert.Equal(MicrophoneActivation.WhileDictating, options.Activation);
    }

    [Fact]
    public void TheOnDemandGraceWindowHandsAHeadsetBackQuickly()
    {
        // Eight seconds: long enough that a correction straight after a dictation finds a warm
        // microphone, short enough that music through a Bluetooth headset is back in stereo
        // before the next track. A longer window finds a warm microphone more often (53% of the
        // dictations in the field log at eight seconds, 71% at sixty) but keeps the headset in its
        // call profile for that long after every dictation, and the owner chose the music. The
        // slider in Settings runs from zero to five minutes for anyone who weighs it differently.
        var options = new AudioCaptureOptions();

        Assert.Equal(TimeSpan.FromSeconds(8), options.IdleRelease);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
