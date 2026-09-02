using Jane.Core.Diagnostics;
using Jane.Speech.Diagnostics;

namespace Jane.Speech.Tests;

public sealed class SherpaNativeProbeTests
{
    [Fact]
    public async Task NativeLibraryLoads_WithTheManagedAndRuntimePackagesInAgreement()
    {
        // org.k2fsa.sherpa.onnx and org.k2fsa.sherpa.onnx.runtime.win-x64 version independently
        // upstream, so restore can succeed with a managed wrapper bound to a native library of a
        // different ABI. Without this test that mismatch first appears as an
        // EntryPointNotFoundException during a user's dictation.
        var result = await new SherpaNativeProbe().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Pass, result.Status);
        Assert.Null(result.Remedy);
    }

    [Fact]
    public async Task ProbeReportsWhichNativeBinariesItFound()
    {
        var result = await new SherpaNativeProbe().RunAsync(TestContext.Current.CancellationToken);

        // The natives ship under runtimes/<rid>/native/, so the probe must resolve them the way
        // the CLR will at the first P/Invoke rather than looking for files next to the assembly.
        Assert.Equal("loaded", result.Data!["sherpa-onnx-c-api"]);
        Assert.Equal("loaded", result.Data["onnxruntime"]);
        Assert.Equal("win-x64", result.Data["rid"]);
    }
}
