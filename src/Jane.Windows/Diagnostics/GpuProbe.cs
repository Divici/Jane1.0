using System.Globalization;
using Jane.Core.Diagnostics;
using Jane.Windows.Gpu;

namespace Jane.Windows.Diagnostics;

/// <summary>Reports the GPU Jane may use for the LLM, and how much VRAM is free right now.</summary>
/// <remarks>
/// A missing GPU is a <see cref="ProbeStatus.Warn"/>, never a Fail. ASR runs on the CPU by
/// design and the LLM formatting pass is optional -- Jane degrades to injecting raw Parakeet
/// output, which already carries punctuation and casing.
/// </remarks>
public sealed class GpuProbe : IProbe
{
    public string Name => "gpu.nvml";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        using var nvml = new NvmlInterop();

        if (!nvml.TryInitialise(out var error))
        {
            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Warn,
                $"NVML unavailable: {error}",
                Remedy: "Install or repair the NVIDIA display driver. Without it Jane cannot detect GPU contention, so it will keep the LLM on the CPU or skip it."));
        }

        var gpus = nvml.Snapshot();
        if (gpus.Count == 0)
        {
            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Warn,
                "NVML initialised but reported no devices.",
                Remedy: "Jane will run ASR on the CPU and skip the LLM formatting pass."));
        }

        var primary = gpus[0];
        var freeGb = primary.FreeBytes / (double)(1024 * 1024 * 1024);
        var totalGb = primary.TotalBytes / (double)(1024 * 1024 * 1024);

        var data = new Dictionary<string, string>
        {
            ["name"] = primary.Name,
            ["device_count"] = gpus.Count.ToString(CultureInfo.InvariantCulture),
            ["total_bytes"] = primary.TotalBytes.ToString(CultureInfo.InvariantCulture),
            ["free_bytes"] = primary.FreeBytes.ToString(CultureInfo.InvariantCulture),
            ["gpu_util_pct"] = primary.GpuUtilisationPercent.ToString(CultureInfo.InvariantCulture),
        };

        return Task.FromResult(new ProbeResult(
            Name,
            ProbeStatus.Pass,
            $"{primary.Name}: {freeGb:F1} of {totalGb:F1} GB VRAM free, {primary.GpuUtilisationPercent}% utilised.",
            Data: data));
    }
}
