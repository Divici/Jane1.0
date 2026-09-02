namespace Jane.Eval;

/// <summary>
/// The eval harness proper lands in Phase 12. What lives here from Phase 0 are the structural
/// invariants the whole plan rests on -- the ones that would silently rot if nothing asserted
/// them until the end.
/// </summary>
public sealed class SolutionShapeTests
{
    [Fact]
    public void SherpaOnnxManagedAndRuntimePackages_ArePinnedToTheSameVersion()
    {
        // These two packages version independently upstream. Restore succeeds either way; the
        // mismatch only shows up as a native entry-point failure at first transcription.
        var props = File.ReadAllText(FindRepoFile("Directory.Build.props"));

        var managed = Between(props, "<SherpaOnnxVersion>", "</SherpaOnnxVersion>");
        var runtime = Between(props, "<SherpaOnnxRuntimeVersion>", "</SherpaOnnxRuntimeVersion>");

        Assert.False(string.IsNullOrWhiteSpace(managed));
        Assert.Equal(managed, runtime);
    }

    [Theory]
    [InlineData("src/Jane.Core/Jane.Core.csproj", "net10.0")]
    [InlineData("src/Jane.Speech/Jane.Speech.csproj", "net10.0")]
    [InlineData("src/Jane.Llm/Jane.Llm.csproj", "net10.0")]
    [InlineData("src/Jane.Windows/Jane.Windows.csproj", "net10.0-windows")]
    [InlineData("src/Jane.App/Jane.App.csproj", "net10.0-windows")]
    public void EveryProjectPinsItsIntendedTargetFramework(string project, string expected)
    {
        // .NET 6 is also installed on this machine, and Jane.Core in particular must stay off
        // the Windows TFM or the orchestrator stops being testable without a desktop session.
        var csproj = File.ReadAllText(FindRepoFile(project.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(expected, Between(csproj, "<TargetFramework>", "</TargetFramework>"));
    }

    [Fact]
    public void ModelWeightsAreNotCommitted()
    {
        // Weights are fetched and SHA-256-verified into %LOCALAPPDATA%\Jane\models on first run.
        // A 622 MB encoder in git history cannot be removed later without rewriting it.
        var gitignore = File.ReadAllText(FindRepoFile(".gitignore"));

        foreach (var pattern in (string[])["*.onnx", "*.gguf", "*.bin", "tools/"])
        {
            Assert.Contains(pattern, gitignore, StringComparison.Ordinal);
        }
    }

    private static string Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : text[start..end].Trim();
    }

    internal static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} walking up from {AppContext.BaseDirectory}.");
    }
}
