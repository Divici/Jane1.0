using Jane.Core.Diagnostics;
using Jane.Windows.Diagnostics;
using Jane.Windows.Gpu;

namespace Jane.Windows.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task GpuProbe_NeverFails_BecauseAMissingGpuIsNotAFaultForJane()
    {
        // ASR is CPU-side by design and the LLM pass is optional, so no GPU means "reduced",
        // never "broken". A Fail here would block a machine that dictates perfectly well.
        var result = await new GpuProbe().RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(ProbeStatus.Fail, result.Status);
        if (result.Status != ProbeStatus.Pass)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
        }
    }

    [Fact]
    public async Task MicrophoneProbe_ReportsAVerdictWithoutOpeningTheDevice()
    {
        // Opening a stream here would take the device and flicker the mic-in-use indicator.
        // Whatever the verdict, a non-Pass must carry a remedy.
        var result = await new MicrophoneProbe().RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Status, (ProbeStatus[])[ProbeStatus.Pass, ProbeStatus.Fail]);
        if (result.Status == ProbeStatus.Fail)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
        }
        else
        {
            Assert.True(int.Parse(result.Data!["device_count"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        }
    }

    [Fact]
    public async Task PortOwnerProbe_PassesForAPortNothingCouldBeListeningOn()
    {
        // Port 1 is reserved and never bound by a user-mode server, so this pins the "clear"
        // branch without depending on whatever happens to be running on the machine.
        var result = await new PortOwnerProbe(port: 1).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Pass, result.Status);
        Assert.Equal("0", result.Data!["listener_count"]);
    }

    [Fact]
    public void NvmlInterop_ReportsAvailabilityWithoutThrowingOnAMachineWithoutIt()
    {
        // NVML ships with the display driver. Its absence must degrade, never crash.
        _ = NvmlInterop.IsAvailable;

        using var nvml = new NvmlInterop();
        if (nvml.TryInitialise(out var error))
        {
            Assert.Null(error);
            var snapshot = nvml.Snapshot();
            Assert.All(snapshot, gpu =>
            {
                Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
                Assert.True(gpu.TotalBytes >= gpu.FreeBytes);
            });
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void NvmlInterop_IsSafeToDisposeTwice()
    {
        var nvml = new NvmlInterop();
        _ = nvml.TryInitialise(out _);

        nvml.Dispose();
        nvml.Dispose();
    }

    [Fact]
    public void NvmlInterop_SnapshotOnAnUninitialisedInstanceIsEmptyRatherThanAnException()
    {
        using var nvml = new NvmlInterop();

        Assert.Empty(nvml.Snapshot());
    }
}
