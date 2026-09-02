using System.Globalization;
using Jane.Core.Abstractions;
using Jane.Core.Diagnostics;

namespace Jane.Llm.Diagnostics;

/// <summary>
/// The probe that decides Phase 6's shape: does per-request <c>options.num_gpu: 0</c> genuinely
/// pin a model to the CPU?
/// </summary>
/// <remarks>
/// If it does, one supervised server serves both routes and the plan's second CPU-pinned
/// instance is unnecessary. If it does not, Jane needs a second server started with
/// <c>OLLAMA_LLM_LIBRARY=cpu</c> -- which is itself only Confidence M and would need the same
/// assertion anyway.
///
/// Pinning is verified, never trusted: the check is <c>/api/ps</c> reporting
/// <c>size_vram == 0</c> for the model after a real inference, not the absence of an error.
/// A CPU route that silently loaded onto the GPU is exactly the failure the in-game fallback
/// exists to prevent.
/// </remarks>
public sealed class CpuPinningProbe(OllamaChatClient client, string model) : IProbe
{
    public string Name => "ollama.num_gpu_zero";

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string> { ["model"] = model };

        // Start clean: a model already resident from a GPU request would be reused as-is and
        // the probe would report the previous placement rather than the one it asked for.
        try
        {
            await client.UnloadAsync(model, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Nothing loaded, or the server does not know the model yet. The chat below reports.
        }

        LlmResponse response;
        try
        {
            response = await client.ChatAsync(
                new LlmRequest(model, "Reply with exactly: OK", "Reply with exactly: OK",
                    Device: LlmDevice.Cpu, NumCtx: 2048, KeepAlive: "30s", MaxTokens: 8),
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(
                Name, ProbeStatus.Fail, $"CPU-pinned inference failed: {ex.Message}",
                Remedy: $"Confirm the model is pulled: tools/ollama/ollama.exe pull {model}.",
                Data: data);
        }

        data["reply"] = response.Text.Trim();
        data["completion_tokens"] = response.CompletionTokens.ToString(CultureInfo.InvariantCulture);

        var loaded = await client.ListLoadedAsync(cancellationToken);
        var entry = loaded.FirstOrDefault(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return new ProbeResult(
                Name, ProbeStatus.Fail,
                $"Inference succeeded but /api/ps does not list {model}, so its placement cannot be verified.",
                Remedy: "Jane will disable the CPU-LLM route and skip the LLM while a game is running, which is the default behaviour anyway.",
                Data: data);
        }

        data["size_vram"] = entry.SizeVramBytes.ToString(CultureInfo.InvariantCulture);
        data["size"] = entry.SizeBytes.ToString(CultureInfo.InvariantCulture);

        if (entry.SizeVramBytes != 0)
        {
            return new ProbeResult(
                Name, ProbeStatus.Fail,
                $"options.num_gpu=0 was ignored: {model} holds {entry.SizeVramBytes / 1024 / 1024} MB of VRAM.",
                Remedy: "The CPU-LLM route is unavailable on this Ollama build. Jane will skip the LLM entirely while a game is detected rather than contend for the GPU.",
                Data: data);
        }

        return new ProbeResult(
            Name, ProbeStatus.Pass,
            $"options.num_gpu=0 pinned {model} to the CPU (size_vram=0). One supervised server can serve both routes.",
            Data: data);
    }
}
