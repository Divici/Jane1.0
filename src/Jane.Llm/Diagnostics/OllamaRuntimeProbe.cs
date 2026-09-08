using Jane.Core.Diagnostics;

namespace Jane.Llm.Diagnostics;

/// <summary>
/// Says whether a model runtime exists on this machine at all, and where it looked.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="OllamaBinaryProbe"/>, which asks whether one specific path is a
/// runnable binary. This asks the question that was actually wrong in the field: <em>is there one
/// anywhere</em>. The published build shipped without <c>tools\ollama</c>, and every symptom the
/// user saw -- two models stuck on "Not downloaded", a Download button that failed -- was
/// downstream of a question nobody was asking.
/// </para>
/// <para>
/// A missing runtime is a <see cref="ProbeStatus.Warn"/>, not a failure. Jane dictates without it:
/// Parakeet already emits punctuation and casing, and skipping the language model is exactly what
/// the in-game route does on purpose.
/// </para>
/// </remarks>
public sealed class OllamaRuntimeProbe(OllamaSearchOptions? search = null) : IProbe
{
    private readonly OllamaSearchOptions _search =
        search ?? OllamaSearchOptions.ForMachine(AppContext.BaseDirectory);

    public string Name => "ollama.runtime";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var location = OllamaLocator.Locate(_search);

        var data = new Dictionary<string, string>
        {
            ["searched"] = string.Join(";", location.Searched),
        };

        if (location.Runtime is { } runtime)
        {
            data["path"] = runtime.ExePath;
            data["source"] = runtime.Source.ToString();

            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Pass, location.Describe(), Data: data));
        }

        return Task.FromResult(new ProbeResult(
            Name,
            ProbeStatus.Warn,
            location.Describe(),
            Remedy: "Open Jane's settings, go to Models, and use the Ollama runtime row to download it. Transcript cleanup is off until then; dictation itself is unaffected.",
            Data: data));
    }
}
