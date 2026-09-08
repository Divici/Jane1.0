using Jane.Llm;

namespace Jane.Llm.Tests;

/// <summary>
/// Finding the model runtime, and saying where it looked when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The field report was "the Qwen models say Not downloaded and the Download button does nothing".
/// The models were never the problem. <c>JaneHost</c> resolved <c>ollama.exe</c> by walking up from
/// <c>AppContext.BaseDirectory</c> looking for a <c>tools\ollama</c> directory -- which exists in a
/// repository checkout and does not exist in <c>C:\Program Files\Jane</c>, because the installer
/// only bundles it behind an opt-in switch. With no runtime the supervised server never started,
/// every presence probe asked a socket nobody was listening on, and the UI reported the honest
/// answer to the wrong question.
/// </para>
/// <para>
/// One directory probe with no fallbacks and no error was the whole failure, so this looks in
/// every place a runtime can reasonably be, in priority order, and a miss names all of them.
/// </para>
/// </remarks>
public sealed class OllamaLocatorTests
{
    [Fact]
    public void TheEnvironmentOverrideWinsOverEverythingElse()
    {
        var options = Search(
            present: ["C:\\override\\ollama.exe", "C:\\install\\tools\\ollama\\ollama.exe"],
            environmentOverride: "C:\\override\\ollama.exe");

        var location = OllamaLocator.Locate(options);

        Assert.True(location.Found);
        Assert.Equal("C:\\override\\ollama.exe", location.Runtime!.ExePath);
        Assert.Equal(OllamaRuntimeSource.EnvironmentOverride, location.Runtime.Source);
    }

    [Fact]
    public void AnOverridePointingAtNothingFallsThroughButSaysSo()
    {
        // Silently ignoring an explicit instruction is how somebody loses an hour. Falling back is
        // still the right behaviour -- a stale variable must not break a working machine -- but the
        // note is what makes the fallback discoverable in the log.
        var options = Search(
            present: ["C:\\install\\tools\\ollama\\ollama.exe"],
            environmentOverride: "C:\\gone\\ollama.exe");

        var location = OllamaLocator.Locate(options);

        Assert.True(location.Found);
        Assert.Equal(OllamaRuntimeSource.InstallDirectory, location.Runtime!.Source);
        Assert.Contains(location.Notes, note => note.Contains("C:\\gone\\ollama.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void TheInstallDirectoryComesBeforeTheUserProfileCopy()
    {
        // A bundled runtime is the one the installer verified. A copy under the profile is the one
        // Jane downloaded for itself, which is the fallback rather than the preference.
        var options = Search(present:
        [
            "C:\\install\\tools\\ollama\\ollama.exe",
            "C:\\profile\\Jane\\tools\\ollama\\ollama.exe",
        ]);

        var location = OllamaLocator.Locate(options);

        Assert.Equal("C:\\install\\tools\\ollama\\ollama.exe", location.Runtime!.ExePath);
        Assert.Equal(OllamaRuntimeSource.InstallDirectory, location.Runtime.Source);
    }

    [Fact]
    public void TheProfileCopyIsUsedWhenTheInstallHasNoBundledRuntime()
    {
        // Exactly the shipped configuration: the installer keeps its download small, and Jane
        // fetches the runtime into the profile on demand.
        var options = Search(present: ["C:\\profile\\Jane\\tools\\ollama\\ollama.exe"]);

        var location = OllamaLocator.Locate(options);

        Assert.Equal(OllamaRuntimeSource.UserProfile, location.Runtime!.Source);
    }

    [Fact]
    public void AnOllamaTheUserAlreadyInstalledThemselvesCounts()
    {
        // Somebody with the official desktop install should not be made to download a second
        // 1.4 GB copy of the same binary.
        var options = Search(present: ["C:\\profile\\Programs\\Ollama\\ollama.exe"]);

        var location = OllamaLocator.Locate(options);

        Assert.Equal(OllamaRuntimeSource.SystemInstall, location.Runtime!.Source);
    }

    [Fact]
    public void AnOllamaOnThePathCounts()
    {
        var options = Search(present: ["D:\\tools\\bin\\ollama.exe"]) with
        {
            PathDirectories = ["D:\\tools\\bin"],
        };

        var location = OllamaLocator.Locate(options);

        Assert.Equal(OllamaRuntimeSource.Path, location.Runtime!.Source);
        Assert.Equal("D:\\tools\\bin\\ollama.exe", location.Runtime.ExePath);
    }

    [Fact]
    public void AMissNamesEveryPlaceItLooked()
    {
        // The old failure said nothing at all. A user who is told the four paths that were tried
        // can see in one line that the installer left the runtime out.
        var location = OllamaLocator.Locate(Search(present: []));

        Assert.False(location.Found);
        Assert.Null(location.Runtime);
        Assert.Contains("C:\\install\\tools\\ollama\\ollama.exe", location.Searched);
        Assert.Contains("C:\\profile\\Jane\\tools\\ollama\\ollama.exe", location.Searched);
        Assert.Contains("C:\\profile\\Programs\\Ollama\\ollama.exe", location.Searched);
        Assert.Contains("could not be found", location.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSearchListHasNoDuplicatesWhenPathAlreadyHoldsAKnownDirectory()
    {
        var options = Search(present: []) with { PathDirectories = ["C:\\install\\tools\\ollama"] };

        var location = OllamaLocator.Locate(options);

        Assert.Equal(
            location.Searched.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            location.Searched.Count);
    }

    [Fact]
    public void TheRealMachineSearchIsBuiltWithoutTouchingTheDisk()
    {
        // ForMachine only composes paths. Nothing here may create a directory or probe a device --
        // it runs during composition, before Jane has decided it needs a model runtime at all.
        var options = OllamaSearchOptions.ForMachine("C:\\Program Files\\Jane");

        Assert.Equal(
            Path.Combine("C:\\Program Files\\Jane", "tools", "ollama", "ollama.exe"),
            options.InstallExe);
        Assert.EndsWith(Path.Combine("Jane", "tools", "ollama", "ollama.exe"), options.UserExe, StringComparison.Ordinal);
        Assert.NotEmpty(options.SystemExes);
    }

    [Fact]
    public void TheProfileDirectoryIsWhereJaneDownloadsItsOwnCopy()
    {
        // Named once, here, because the in-app installer writes to it and the locator reads it,
        // and a mismatch between those two would look exactly like a download that did nothing.
        var options = OllamaSearchOptions.ForMachine("C:\\Program Files\\Jane");

        Assert.Equal(Path.Combine(OllamaLocator.UserRuntimeDirectory, "ollama.exe"), options.UserExe);
    }

    private static OllamaSearchOptions Search(
        IReadOnlyCollection<string> present, string? environmentOverride = null)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);

        return new OllamaSearchOptions
        {
            EnvironmentOverride = environmentOverride,
            InstallExe = "C:\\install\\tools\\ollama\\ollama.exe",
            UserExe = "C:\\profile\\Jane\\tools\\ollama\\ollama.exe",
            SystemExes = ["C:\\profile\\Programs\\Ollama\\ollama.exe"],
            PathDirectories = [],
            Exists = set.Contains,
        };
    }
}
