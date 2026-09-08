using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// The microphone stays off until the key is pressed, and the first word survives anyway.
/// </summary>
/// <remarks>
/// <para>
/// The field report was that the first couple of words are cut off every time. The cause is in
/// <see cref="MicrophoneActivationTests"/>'s own documentation: opening the device on key-down
/// costs 100-400 ms of driver work, and the user is already a syllable in by the time the first
/// sample arrives. The pre-roll buffer cannot help, because there is nothing to back-date -- the
/// device was closed when they started speaking.
/// </para>
/// <para>
/// The obvious fix is to hold the device open, and it is the wrong one. That is what
/// <see cref="MicrophoneActivation.AlwaysOpen"/> already does, and it forces a Bluetooth headset
/// into its narrowband call profile for as long as Jane is running -- degrading every other sound
/// on the machine. The user asked for the microphone to stay off until the key is pressed, and
/// that constraint stands.
/// </para>
/// <para>
/// So the two halves of "open" are separated. <c>Prepare</c> creates and initialises the audio
/// client, which allocates the endpoint buffer and nothing else: no samples flow, no capture
/// indicator lights, no profile switch. <c>Start</c> activates the stream, and that is the only
/// part that happens on key-down -- single-digit milliseconds instead of several hundred. Idle
/// release stops the stream again rather than tearing the client down, so the endpoint is
/// inactive within seconds of the last dictation exactly as before.
/// </para>
/// </remarks>
public sealed class WarmMicrophoneTests
{
    private static readonly AudioCaptureOptions OnDemand = new()
    {
        PreRoll = TimeSpan.FromMilliseconds(500),
        ReconnectInterval = TimeSpan.FromMilliseconds(10),
        Activation = MicrophoneActivation.WhileDictating,
        IdleRelease = TimeSpan.FromMilliseconds(60),
    };

    [Fact]
    public async Task StartupReadiesTheDeviceWithoutActivatingIt()
    {
        // Prepared, not running. This is the whole trade: the expensive half is paid while the
        // tray icon is appearing, and the half that a headset can hear is not paid at all.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        Assert.Equal(1, devices.OpenCount);
        Assert.True(devices.Current!.IsPrepared);
        Assert.False(devices.Current.IsRunning);

        // IsOpen means "capturing is happening", which is what the overlay and settings show. A
        // prepared stream is not open by that definition, and must not look like it.
        Assert.False(capture.State.IsOpen);
    }

    [Fact]
    public async Task TheKeyPressCostsAStartRatherThanAnOpen()
    {
        // The measurement that matters. A device whose open takes 300 ms and whose start takes
        // almost nothing must produce a key-down that costs almost nothing.
        var devices = new FakeCaptureDeviceFactory { OpenDelay = TimeSpan.FromMilliseconds(300) };
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        var started = Stopwatch.GetTimestamp();
        capture.Arm();
        await capture.Armed;
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(devices.Current!.IsRunning);
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(150),
            $"Arming took {elapsed.TotalMilliseconds:F0} ms; the device open must not be on the key-down path.");
    }

    [Fact]
    public async Task SpeechAHundredMillisecondsAfterTheKeyIsKept()
    {
        // The reported symptom, stated as an assertion. Under the old behaviour the device was
        // still opening at this point and these samples did not exist.
        var devices = new FakeCaptureDeviceFactory { OpenDelay = TimeSpan.FromMilliseconds(300) };
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(Tone(AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(400))));

        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(400)), audio.Samples.Length);
    }

    [Fact]
    public async Task TheGraceWindowStopsTheStreamButKeepsTheClientReady()
    {
        // Stopping is what makes the endpoint inactive, which is what a Bluetooth headset reacts
        // to. Keeping the client is what makes the next key press cheap.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        capture.Arm();
        await capture.Armed;
        _ = capture.Stop(CaptureStopReason.Released);

        await WaitUntil(() => !devices.Current!.IsRunning);

        Assert.False(devices.Current!.IsRunning);
        Assert.False(devices.Current.IsDisposed);
        Assert.True(devices.Current.IsPrepared);
        Assert.Equal(1, devices.OpenCount);
    }

    [Fact]
    public async Task AStoppedStreamThrowsAwayItsPreRollSoNothingStaleIsBackDated()
    {
        // A ring holding the tail of the previous dictation would splice minutes-old audio onto
        // the front of the next one -- a far stranger transcript than a missing syllable.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        capture.Arm();
        await capture.Armed;
        devices.Current!.Emit(Tone(AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(200))));
        _ = capture.Stop(CaptureStopReason.Released);

        await WaitUntil(() => !devices.Current!.IsRunning);

        capture.Arm();
        await capture.Armed;
        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(0, audio.PreRollSamples);
    }

    [Fact]
    public async Task ASecondDictationInsideTheGraceWindowStillGetsItsPreRoll()
    {
        // The stream is still running here, so the moments before the second key-down genuinely
        // were captured, and back-dating them is correct rather than stale.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(
            devices, OnDemand with { IdleRelease = TimeSpan.FromSeconds(30) });

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        capture.Arm();
        await capture.Armed;
        _ = capture.Stop(CaptureStopReason.Released);

        devices.Current!.Emit(Tone(AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(200))));

        capture.Arm();
        await capture.Armed;
        var audio = capture.Stop(CaptureStopReason.Released);

        Assert.Equal(AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(200)), audio.PreRollSamples);
    }

    [Fact]
    public async Task NoSamplesFlowWhileTheStreamIsMerelyPrepared()
    {
        // A prepared client that leaked audio would be an always-open microphone wearing a
        // different name, which is the thing this design exists not to be.
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        Assert.False(devices.Current!.IsRunning);
        Assert.Equal(0, devices.Current.StartCount);
    }

    [Fact]
    public async Task ChangingTheMicrophoneTearsTheOldClientDownAndReadiesTheNewOne()
    {
        var devices = new FakeCaptureDeviceFactory();
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;
        var first = devices.Current!;

        capture.Reconfigure(new MicrophoneRouting("mic-2", MicrophoneActivation.WhileDictating, TimeSpan.FromMilliseconds(60)));
        await WaitUntil(() => devices.OpenCount == 2);
        await capture.Armed;

        Assert.True(first.IsDisposed);
        Assert.Equal("mic-2", devices.Requested[^1]);
        Assert.True(devices.Current!.IsPrepared);
        Assert.False(devices.Current.IsRunning);
    }

    [Fact]
    public async Task ADeviceThatCannotBePreparedDoesNotStopJaneStarting()
    {
        // Readying the microphone at startup must never be able to take the app down with it: the
        // failure belongs on the pill, and the next key press retries.
        var devices = new FakeCaptureDeviceFactory
        {
            OpenFailure = new AudioDeviceException(AudioDeviceFailure.OpenFailed, "Exclusive-mode owner."),
        };
        await using var capture = new WasapiCapture(devices, OnDemand);

        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        Assert.False(capture.State.IsOpen);
        Assert.Contains("Exclusive-mode", capture.State.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreRollWindowCoversAWholeSecond()
    {
        // Half a second was chosen when the device was held open and the only gap to cover was
        // between "started speaking" and "key registered". A second costs 16k floats and covers a
        // slow start as well.
        Assert.Equal(TimeSpan.FromSeconds(1), PreRollBuffer.DefaultWindow);
    }

    private static float[] Tone(int samples)
    {
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            buffer[i] = (float)(Math.Sin(i * 0.05) * 0.2);
        }

        return buffer;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting for the capture state to settle.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
