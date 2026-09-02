using Jane.Core.Platform;

namespace Jane.Bench;

/// <summary>Locations and names shared by every bench sub-command.</summary>
public sealed class JaneEnvironment
{
    public const string EnvOllamaGpuUrl = "JANE_OLLAMA_GPU_URL";

    public JaneEnvironment(string? repoRoot = null)
    {
        RepoRoot = repoRoot ?? FindRepoRoot();
        Paths = new JanePaths();
        OllamaExe = Path.Combine(RepoRoot, "tools", "ollama", "ollama.exe");

        var url = Environment.GetEnvironmentVariable(EnvOllamaGpuUrl);
        OllamaHost = string.IsNullOrWhiteSpace(url)
            ? "127.0.0.1:11435"
            : new Uri(url).Authority;
    }

    public string RepoRoot { get; }

    public JanePaths Paths { get; }

    public string OllamaExe { get; }

    /// <summary>Loopback only, and deliberately off Ollama's default :11434.</summary>
    public string OllamaHost { get; }

    public string OllamaBaseUrl => $"http://{OllamaHost}";

    /// <summary>GPU-route formatting model: a dedicated non-thinking checkpoint (plan.md P0-2).</summary>
    public string GpuModel => "jane-qwen3-4b";

    /// <summary>CPU-route fallback, used only when the user opts into formatting while gaming.</summary>
    public string CpuModel => "jane-qwen3-1.7b";

    /// <summary>
    /// Walks up from the running binary to the directory holding Jane.sln. `dotnet run` starts
    /// in bin/Debug/net10.0-windows, so a relative "tools/ollama" would otherwise miss.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Jane.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
