using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// When Jane holds the microphone open.
/// </summary>
/// <remarks>
/// <para>
/// The plan chose to open the device at app start and hold it open forever, so that a key press
/// never waits on a driver and the pre-roll buffer always has the moment before the key went
/// down. It priced the cost as "a permanent mic-in-use indicator, which onboarding discloses".
/// </para>
/// <para>
/// That price was wrong, and only wrong on hardware the plan never tried. A Bluetooth headset has
/// two profiles: A2DP, which is stereo and high bitrate and has no microphone at all, and HFP,
/// which has a microphone and drops playback to a narrowband mono call channel. Windows switches
/// to HFP whenever <em>any</em> process holds a capture stream on the headset. So an always-open
/// microphone does not cost an icon -- it costs every other sound on the machine, permanently,
/// and the user has no way to connect the two facts.
/// </para>
/// <para>
/// Hence <see cref="MicrophoneActivation.WhileDictating"/>, and hence it being the default. The
/// honest cost is stated in <see cref="AColdOpenHasNoPreRoll"/>: opening on key-down cannot
/// back-date audio that was never captured. The idle-release grace window is what stops a run of
/// quick dictations paying that cost more than once.
/// </para>
/// </remarks>
public sealed class MicrophoneActivationTests
{
    private static readonly AudioCaptureOptions OnDemand = new()
    {
        PreRoll = TimeSpan.FromMilliseconds(500),
        ReconnectInterval = TimeSpan.FromMilliseconds(10),
        Activation = MicrophoneActivation.WhileDictating,
        IdleRelease = TimeSpan.FromMilliseconds(60),
    };

    [Fact]
    public async Task StartupNeverActivatesTheEndpoint()
    {
        // The headline of the change, restated after the clipped-first-word fix: Jane sitting in
        // the tray must not be capturing, or a Bluetooth headset stays in call mode all day. What
        // it may do is have the device resolved and ready -- that part is silent, and paying for
        // it here rather than on key-down is what stops the first word going missing.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        Assert.False(devices.Current!.IsRunning);
        Assert.Equal(0, devices.Current.StartCount);
        Assert.False(capture.State.IsOpen);
    }

    [Fact]
    public async Task ArmingOpensTheDeviceAndCapturesWhatFollows()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;

        Assert.Equal(1, devices.OpenCount);
        Assert.True(capture.State.IsOpen);

        devices.Current!.Emit(new float[1600]);
        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(1600, captured.Samples.Length);
    }

    [Fact]
    public async Task AColdOpenHasNoPreRoll()
    {
        // Stated rather than worked around. A device that was closed when the key went down
        // produced no samples to back-date, so the pre-roll is genuinely zero and the first
        // syllable is at risk. That is the price of not holding the microphone open, and the
        // grace window below is what keeps it from being paid on every dictation.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(new float[800]);

        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(0, captured.PreRollSamples);
    }

    [Fact]
    public async Task TheEndpointGoesInactiveOnceTheGraceWindowExpires()
    {
        // The user-visible promise is unchanged -- the microphone is off between dictations. It is
        // kept by releasing the recorder, because a capture stream is single-use and one that has
        // been started cannot be started again. A replacement is readied straight away, so the
        // next key press still costs only a start.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        var stream = devices.Current!;
        capture.Stop(CaptureStopReason.Released);

        await WaitUntil(() => stream.IsDisposed, "the capture stream to be released");
        await capture.Armed;

        Assert.False(capture.State.IsOpen);
        Assert.False(stream.IsRunning);
        Assert.True(devices.Current!.IsPrepared, "a replacement must be readied for the next press");
        Assert.False(devices.Current.IsRunning);
    }

    [Fact]
    public async Task ASecondDictationInsideTheGraceWindowReusesTheOpenDevice()
    {
        // Bluetooth profile switching is not free -- it is the audible pop, and up to a second of
        // it. Someone dictating three sentences in a row must pay that once, not three times,
        // and their second sentence gets its pre-roll back as a side effect.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand with
        {
            IdleRelease = TimeSpan.FromSeconds(30),
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        capture.Stop(CaptureStopReason.Released);

        devices.Current!.Emit(new float[1600]);
        capture.Arm();
        await capture.Armed;

        Assert.Equal(1, devices.OpenCount);
        Assert.Equal(1600, capture.Stop(CaptureStopReason.Released).PreRollSamples);
    }

    [Fact]
    public async Task TheGraceWindowNeverClosesADeviceThatIsCapturingAgain()
    {
        // The release is scheduled at Stop and fires later, so it has to re-check: a dictation
        // started in the meantime must not have its microphone pulled out from under it.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        capture.Stop(CaptureStopReason.Released);
        capture.Arm();
        await capture.Armed;

        await Task.Delay(OnDemand.IdleRelease * 4, TestContext.Current.CancellationToken);

        Assert.True(capture.State.IsOpen);
        Assert.True(capture.State.IsCapturing);
    }

    [Fact]
    public async Task ReconfiguringWithTheSameRoutingLeavesAWarmDeviceAlone()
    {
        // Every setter in the settings window writes the whole settings object, so the capture is
        // reconfigured whenever anything at all changes. Letting go of a microphone that is
        // sitting inside its grace window because somebody moved an unrelated slider would make
        // the next dictation a needless cold open.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand with
        {
            IdleRelease = TimeSpan.FromSeconds(30),
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;
        capture.Stop(CaptureStopReason.Released);

        capture.Reconfigure(new MicrophoneRouting(
            null, MicrophoneActivation.WhileDictating, TimeSpan.FromSeconds(30)));

        Assert.True(capture.State.IsOpen);
    }

    [Fact]
    public async Task ChangingTheMicrophoneTakesEffectOnTheNextDictation()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Reconfigure(new MicrophoneRouting(
            "{0.0.1.00000000}.{headset}", MicrophoneActivation.WhileDictating, OnDemand.IdleRelease));

        capture.Arm();
        await capture.Armed;

        // The last request is the one that counts: startup readied the default device, and the
        // reconfigure let it go and readied the chosen one.
        Assert.Equal("{0.0.1.00000000}.{headset}", devices.Requested[^1]);
    }

    [Fact]
    public async Task SwitchingToAlwaysOpenOpensTheDeviceImmediately()
    {
        // The opposite direction, for someone on a wired mic who wants the pre-roll back.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;
        Assert.False(capture.State.IsOpen);

        capture.Reconfigure(new MicrophoneRouting(
            null, MicrophoneActivation.AlwaysOpen, OnDemand.IdleRelease));

        await WaitUntil(() => capture.State.IsOpen, "the device to be opened");
    }

    [Fact]
    public async Task SwitchingToWhileDictatingReleasesAnIdleDevice()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand with
        {
            Activation = MicrophoneActivation.AlwaysOpen,
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        Assert.True(capture.State.IsOpen);

        capture.Reconfigure(new MicrophoneRouting(
            null, MicrophoneActivation.WhileDictating, OnDemand.IdleRelease));

        await WaitUntil(() => !capture.State.IsOpen, "the device to be released");
    }

    [Fact]
    public async Task AlwaysOpenStillHoldsTheDeviceOpenAcrossDictations()
    {
        // The old behaviour has to survive intact, because it is still the right choice on a
        // wired microphone and it is what the pre-roll guarantee depends on.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand with
        {
            Activation = MicrophoneActivation.AlwaysOpen,
        });
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        capture.Stop(CaptureStopReason.Released);
        await Task.Delay(OnDemand.IdleRelease * 4, TestContext.Current.CancellationToken);

        Assert.Equal(1, devices.OpenCount);
        Assert.True(capture.State.IsOpen);
    }

    [Fact]
    public async Task ADeviceThatFailsToOpenOnKeyDownReportsItRatherThanHanging()
    {
        // With the device opened at startup, a failure surfaced long before anyone pressed the
        // key. On demand it surfaces mid-dictation, so Stop has to return rather than wait on a
        // stream that is never going to arrive.
        var devices = new FakeCaptureDeviceFactory
        {
            OpenFailure = new AudioDeviceException(AudioDeviceFailure.NoDevice, "No microphone."),
        };
        await using var capture = new WasapiCapture(devices, OnDemand);
        await capture.OpenAsync(TestContext.Current.CancellationToken);

        capture.Arm();
        await capture.Armed;

        var captured = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(0, captured.Samples.Length);
        Assert.False(capture.State.IsOpen);
        Assert.NotNull(capture.State.Error);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
