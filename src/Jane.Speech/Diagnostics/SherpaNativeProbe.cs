using System.Globalization;
using System.Runtime.InteropServices;
using Jane.Core.Diagnostics;

namespace Jane.Speech.Diagnostics;

/// <summary>
/// Confirms the sherpa-onnx native libraries actually load, by making a real call across the
/// P/Invoke boundary rather than checking that a file exists.
/// </summary>
/// <remarks>
/// <c>org.k2fsa.sherpa.onnx</c> and <c>org.k2fsa.sherpa.onnx.runtime.win-x64</c> version
/// independently upstream, so a restore can succeed and still leave a managed wrapper paired
/// with a native library of a different ABI. That mismatch surfaces as an
/// <see cref="EntryPointNotFoundException"/> at the first real transcription -- which, without
/// this probe, would be the user's first dictation.
/// </remarks>
public sealed class SherpaNativeProbe : IProbe
{
    private static readonly string[] NativeLibraries = ["sherpa-onnx-c-api", "onnxruntime"];

    public string Name => "speech.sherpa_native";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>();

        var managed = typeof(SherpaOnnx.OfflineRecognizerConfig).Assembly;
        data["managed_assembly"] = managed.GetName().Name ?? "unknown";
        data["managed_version"] = managed.GetName().Version?.ToString() ?? "unknown";
        data["rid"] = RuntimeInformation.RuntimeIdentifier;

        // The natives ship under runtimes/<rid>/native/, not flat next to the managed assembly.
        // Asking NativeLibrary to resolve them is the only check that matches what the CLR will
        // actually do at the first P/Invoke -- a file-existence test in the wrong directory would
        // report a healthy install as broken, and vice versa.
        foreach (var library in NativeLibraries)
        {
            if (NativeLibrary.TryLoad(library, managed, searchPath: null, out var handle))
            {
                data[library] = "loaded";
                NativeLibrary.Free(handle);
            }
            else
            {
                data[library] = "unresolved";
            }
        }

        var unresolved = NativeLibraries.Where(l => data[l] == "unresolved").ToArray();
        if (unresolved.Length > 0)
        {
            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Fail,
                $"sherpa-onnx native libraries did not resolve: {string.Join(", ", unresolved)}.",
                Remedy: "The win-x64 runtime package did not restore, or the managed and native sherpa-onnx packages have drifted apart. Check that SherpaOnnxVersion and SherpaOnnxRuntimeVersion in Directory.Build.props match, then run `dotnet restore --force`.",
                Data: data));
        }

        try
        {
            // Constructing a config and reading it back is the cheapest call that genuinely
            // crosses the boundary -- no model files, no session allocation.
            var config = new SherpaOnnx.OfflineRecognizerConfig();
            config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            config.ModelConfig.Provider = "cpu";
            config.DecodingMethod = "greedy_search";
            data["default_num_threads"] = config.ModelConfig.NumThreads.ToString(CultureInfo.InvariantCulture);

            var vad = new SherpaOnnx.VadModelConfig();
            vad.SileroVad.Threshold = 0.5f;
            data["vad_threshold"] = vad.SileroVad.Threshold.ToString("F2", CultureInfo.InvariantCulture);

            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Pass,
                $"sherpa-onnx {data["managed_version"]} loaded; native ABI reachable on the CPU provider.",
                Data: data));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Fail,
                $"sherpa-onnx native library failed to load: {ex.GetType().Name}: {ex.Message}",
                Remedy: "The managed and native sherpa-onnx packages have drifted apart. Check that SherpaOnnxVersion and SherpaOnnxRuntimeVersion in Directory.Build.props match, then run `dotnet restore --force`.",
                Data: data));
        }
    }
}
