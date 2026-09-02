using Jane.Core.Diagnostics;

namespace Jane.Llm.Diagnostics;

/// <summary>Confirms Jane's supervised Ollama instance answers at all.</summary>
public sealed class OllamaReachableProbe(OllamaChatClient client, string baseUrl) : IProbe
{
    public string Name => "ollama.reachable";

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var version = await client.GetVersionAsync(cancellationToken);

        if (version is null)
        {
            return new ProbeResult(
                Name,
                ProbeStatus.Fail,
                $"No Ollama server answered at {baseUrl}.",
                Remedy: "Run build/get-ollama.ps1 to fetch the standalone ollama.exe, then start Jane -- it supervises its own instance. To probe by hand: tools/ollama/ollama.exe serve with OLLAMA_HOST=127.0.0.1:11435.",
                Data: new Dictionary<string, string> { ["base_url"] = baseUrl });
        }

        var tags = await client.ListAvailableAsync(cancellationToken);
        return new ProbeResult(
            Name,
            ProbeStatus.Pass,
            $"Ollama {version} at {baseUrl}, {tags.Count} model(s) available.",
            Data: new Dictionary<string, string>
            {
                ["base_url"] = baseUrl,
                ["version"] = version,
                ["models"] = string.Join(", ", tags),
            });
    }
}
