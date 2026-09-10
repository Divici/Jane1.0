using Jane.Core.Abstractions;
using Jane.Windows.Audio;
using NAudio.CoreAudioApi;
using WasapiCapture = Jane.Windows.Audio.WasapiCapture;

namespace Jane.Windows.Tests;

/// <summary>
/// The capture lifecycle, against a real microphone.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this project drives <see cref="WasapiCapture"/> through a fake, which is
/// what makes the routing and failure rules assertable headless. It is also how a whole release
/// shipped believing a capture stream could be stopped and started again: the fake said yes, no
/// real device does, and the first dictation after every idle release failed with "No microphone".
/// </para>
/// <para>
/// So one test touches the hardware, and it touches it in the exact shape a user does -- dictate,
/// let the idle window expire, dictate again. It skips where there is no capture endpoint, in
/// keeping with <c>ContextReaderTests</c>, and it activates the microphone for well under a
/// second: a Bluetooth headset may switch profile once while it runs.
/// </para>
/// </remarks>
[Trait("Category", "RealDevice")]
public sealed class RealMicrophoneTests
{
    private static readonly AudioCaptureOptions Impatient = new()
    {
        PreRoll = TimeSpan.FromMilliseconds(200),
        ReconnectInterval = TimeSpan.FromMilliseconds(50),
        Activation = MicrophoneActivation.WhileDictating,

        // Short enough that a test does not sit through the shipped eight seconds, long enough
        // that the release is not racing the dictation that scheduled it.
        IdleRelease = TimeSpan.FromMilliseconds(150),
    };

    [Fact]
    public async Task TwoDictationsSeparatedByTheIdleWindowBothCapture()
    {
        // The regression, on the hardware that produced it. The first dictation always worked;
        // this test exists for the second.
        Assert.SkipWhen(!HasCaptureDevice(), "No capture endpoint on this machine.");

        await using var capture = new WasapiCapture(new WasapiDeviceFactory(), Impatient);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        var first = await DictateAsync(capture);
        Assert.Null(capture.State.Error);
        Assert.True(first.Samples.Length > 0, "The first dictation captured nothing.");

        // Past the idle window, which is where the recorder is released and rebuilt.
        await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
        await capture.Armed;

        var second = await DictateAsync(capture);

        Assert.Null(capture.State.Error);
        Assert.True(
            second.Samples.Length > 0,
            "The second dictation captured nothing: the device was not usable after the idle release.");
    }

    [Fact]
    public async Task AThirdDictationStillWorks()
    {
        // Two could pass by luck -- a rebuild that happens to work once. Three is a cycle.
        Assert.SkipWhen(!HasCaptureDevice(), "No capture endpoint on this machine.");

        await using var capture = new WasapiCapture(new WasapiDeviceFactory(), Impatient);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        for (var i = 1; i <= 3; i++)
        {
            var audio = await DictateAsync(capture);

            Assert.True(audio.Samples.Length > 0, $"Dictation {i} captured nothing.");
            Assert.Null(capture.State.Error);

            await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
            await capture.Armed;
        }
    }

    [Fact]
    public async Task AReadiedDeviceIsNotCapturingUntilTheKeyGoesDown()
    {
        // The promise the whole design rests on: readying is silent. If this ever fails, Jane is
        // holding a live capture stream while it sits in the tray.
        Assert.SkipWhen(!HasCaptureDevice(), "No capture endpoint on this machine.");

        await using var capture = new WasapiCapture(new WasapiDeviceFactory(), Impatient);
        await capture.OpenAsync(TestContext.Current.CancellationToken);
        await capture.Armed;

        Assert.False(capture.State.IsOpen);
        Assert.Null(capture.State.Error);
    }

    private static async Task<CapturedAudio> DictateAsync(WasapiCapture capture)
    {
        capture.Arm();
        await capture.Armed;

        // Long enough for the device to deliver at least one buffer at the 20 ms period Jane asks
        // for, with room for a driver that is slower to get going.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        return capture.Stop(CaptureStopReason.Released);
    }

    private static bool HasCaptureDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count > 0;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}
