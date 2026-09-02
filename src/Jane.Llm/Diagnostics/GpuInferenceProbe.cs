using System.Globalization;
using Jane.Core.Abstractions;
using Jane.Core.Diagnostics;

namespace Jane.Llm.Diagnostics;

/// <summary>
/// Runs a real inference on the GPU for each quantisation Jane might ship, because
/// <c>ollama#14374</c> / <c>llama.cpp#18331</c>'s sm_120 MMQ crash ("device kernel image is
/// invalid") is quant-dependent -- q4_K_M can work while q8_0 or fp16 crash the runner.
/// </summary>
/// <remarks>
/// A failure here is a <see cref="ProbeStatus.Warn"/>, not a Fail: Jane's ASR is CPU-side and
/// the LLM pass is optional, so a broken GPU route costs formatting, not dictation.
/// </remarks>
public sealed class GpuInferenceProbe(OllamaChatClient client, IReadOnlyList<string> models) : IProbe
{
    public string Name => "ollama.gpu_inference";

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>();
        var available = await client.ListAvailableAsync(cancellationToken);
        var ok = new List<string>();
        var broken = new List<string>();
        var missing = new List<string>();

        foreach (var model in models)
        {
            if (!available.Any(t => t.StartsWith(model, StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add(model);
                data[$"{model}.status"] = "not pulled";
                continue;
            }

            try
            {
                await client.UnloadAsync(model, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Not loaded. Fine -- the point is only to force a fresh GPU placement.
            }

            try
            {
                var response = await client.ChatAsync(
                    new LlmRequest(model, "Reply with exactly: OK", "Reply with exactly: OK",
                        Device: LlmDevice.Gpu, NumCtx: 2048, KeepAlive: "30s", MaxTokens: 8),
                    cancellationToken);

                var loaded = await client.ListLoadedAsync(cancellationToken);
                var entry = loaded.FirstOrDefault(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase));
                var vram = entry?.SizeVramBytes ?? 0;

                data[$"{model}.status"] = "ok";
                data[$"{model}.size_vram"] = vram.ToString(CultureInfo.InvariantCulture);
                data[$"{model}.load_ms"] = response.LoadDuration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
                data[$"{model}.total_ms"] = response.TotalDuration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
                data[$"{model}.completion_tokens"] = response.CompletionTokens.ToString(CultureInfo.InvariantCulture);

                // Zero VRAM on a GPU-routed request means Ollama silently fell back to the CPU.
                // That is the ollama#13163 shape and it must not be reported as a healthy GPU.
                if (vram == 0)
                {
                    data[$"{model}.status"] = "fell back to CPU";
                    broken.Add($"{model} (silent CPU fallback)");
                }
                else
                {
                    ok.Add(model);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OllamaException or TaskCanceledException)
            {
                data[$"{model}.status"] = "failed";
                data[$"{model}.error"] = ex.Message;
                broken.Add($"{model} ({ex.GetType().Name})");
            }
        }

        var summary = string.Join("; ",
            new[]
            {
                ok.Count > 0 ? $"ok: {string.Join(", ", ok)}" : null,
                broken.Count > 0 ? $"failed: {string.Join(", ", broken)}" : null,
                missing.Count > 0 ? $"not pulled: {string.Join(", ", missing)}" : null,
            }.Where(s => s is not null));

        if (broken.Count > 0)
        {
            return new ProbeResult(
                Name, ProbeStatus.Warn,
                $"GPU inference is not reliable across quantisations -- {summary}.",
                Remedy: "This is the open sm_120 MMQ kernel bug (ollama#14374). Jane will use a quantisation that works, or skip the LLM and inject raw ASR text. Dictation is unaffected.",
                Data: data);
        }

        if (ok.Count == 0)
        {
            return new ProbeResult(
                Name, ProbeStatus.Warn,
                $"No GPU inference could be verified -- {summary}.",
                Remedy: "Pull the models first: tools/ollama/ollama.exe pull qwen3:4b-instruct. Until then Jane injects raw ASR text, which already carries punctuation and casing.",
                Data: data);
        }

        return new ProbeResult(
            Name, ProbeStatus.Pass,
            $"Real GPU inference succeeded on sm_120 -- {summary}.",
            Data: data);
    }
}
