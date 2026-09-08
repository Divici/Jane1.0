using Jane.Core.Diagnostics;
using Jane.Windows.Audio;
using Jane.Windows.Diagnostics;

namespace Jane.Windows.Tests;

/// <summary>
/// The claim behind the clipped-first-word fix, checked rather than asserted.
/// </summary>
/// <remarks>
/// Moving the device open off the key-down path only helps if starting an already-readied stream
/// is genuinely cheap, and that is a property of a driver rather than of Jane. A machine where it
/// is not must say so in its own diagnostic report instead of quietly still clipping words.
/// </remarks>
public sealed class MicrophoneLatencyProbeTests
{
    [Fact]
    public async Task AFastStartPassesAndReportsBothHalvesSeparately()
    {
        var devices = new FakeCaptureDeviceFactory { OpenDelay = TimeSpan.FromMilliseconds(120) };

        var result = await new MicrophoneLatencyProbe(devices).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Pass, result.Status);
        Assert.NotNull(result.Data);
        Assert.True(double.Parse(result.Data!["open_ms"], System.Globalization.CultureInfo.InvariantCulture) > 100);
        Assert.True(double.Parse(result.Data["start_ms"], System.Globalization.CultureInfo.InvariantCulture) < 50);
        Assert.Contains("key press", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEndpointIsLeftInactiveWhenTheProbeIsDone()
    {
        // The probe has to start the stream to time it. Leaving it running would hold a headset
        // in call mode after a diagnostic, which is precisely the state Jane refuses to create.
        var devices = new FakeCaptureDeviceFactory();

        _ = await new MicrophoneLatencyProbe(devices).RunAsync(TestContext.Current.CancellationToken);

        Assert.False(devices.Current!.IsRunning);
        Assert.True(devices.Current.IsDisposed);
    }

    [Fact]
    public async Task ADeviceThatCannotBeOpenedIsAWarningWithARemedyRatherThanAThrow()
    {
        var devices = new FakeCaptureDeviceFactory
        {
            OpenFailure = new AudioDeviceException(AudioDeviceFailure.NoDevice, "No capture endpoint."),
        };

        var result = await new MicrophoneLatencyProbe(devices).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Warn, result.Status);
        Assert.NotNull(result.Remedy);
    }

    [Fact]
    public async Task ASlowStartIsAWarningThatNamesTheConsequence()
    {
        // A driver where starting is expensive means the fix did not land on this machine, and
        // the user should be told in the same words the symptom arrives in.
        var devices = new FakeCaptureDeviceFactory { StartDelay = TimeSpan.FromMilliseconds(120) };

        var result = await new MicrophoneLatencyProbe(devices).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Warn, result.Status);
        Assert.Contains("first word", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("All the time", result.Remedy!, StringComparison.Ordinal);
    }
}
