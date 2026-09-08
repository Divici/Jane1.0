using System.Text;

namespace Jane.Llm;

/// <summary>Where a runtime was found. Diagnostic, and the reason a fallback is not silent.</summary>
public enum OllamaRuntimeSource
{
    /// <summary><c>JANE_OLLAMA_EXE</c>. A developer or a support session pointing Jane somewhere.</summary>
    EnvironmentOverride,

    /// <summary><c>&lt;install&gt;\tools\ollama</c> -- bundled by the installer, or the repo checkout.</summary>
    InstallDirectory,

    /// <summary><c>%LOCALAPPDATA%\Jane\tools\ollama</c> -- the copy Jane downloaded for itself.</summary>
    UserProfile,

    /// <summary>The official Ollama install. Reused rather than duplicated.</summary>
    SystemInstall,

    /// <summary>Somewhere on <c>PATH</c>.</summary>
    Path,
}

/// <param name="ExePath">Absolute path to <c>ollama.exe</c>.</param>
public sealed record OllamaRuntime(string ExePath, OllamaRuntimeSource Source);

/// <summary>
/// The answer to "where is the model runtime", including the case where there isn't one.
/// </summary>
/// <remarks>
/// <see cref="Searched"/> matters as much as <see cref="Runtime"/>. The bug this type exists to
/// prevent produced no message at all, and the difference between "the model did not download" and
/// "there was nowhere to download it to" was invisible from the outside.
/// </remarks>
public sealed record OllamaLocation
{
    public OllamaRuntime? Runtime { get; init; }

    /// <summary>Every path considered, in the order they were tried.</summary>
    public IReadOnlyList<string> Searched { get; init; } = [];

    /// <summary>Things worth saying out loud, such as an override that pointed at nothing.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool Found => Runtime is not null;

    /// <summary>One line for the log, the doctor report, and the settings row.</summary>
    public string Describe()
    {
        if (Runtime is { } runtime)
        {
            return $"Model runtime at {runtime.ExePath} ({runtime.Source}).";
        }

        var builder = new StringBuilder("The model runtime (ollama.exe) could not be found. Looked in: ");
        builder.AppendJoin("; ", Searched);
        return builder.Append('.').ToString();
    }
}

/// <param name="EnvironmentOverride">The raw value of <c>JANE_OLLAMA_EXE</c>, or null.</param>
/// <param name="Exists">
/// Injected so the search is a pure function of its inputs. The real one is
/// <see cref="File.Exists(string)"/>; a test supplies a set.
/// </param>
public sealed record OllamaSearchOptions
{
    public string? EnvironmentOverride { get; init; }

    public required string InstallExe { get; init; }

    public required string UserExe { get; init; }

    public IReadOnlyList<string> SystemExes { get; init; } = [];

    public IReadOnlyList<string> PathDirectories { get; init; } = [];

    public Func<string, bool> Exists { get; init; } = File.Exists;

    /// <summary>
    /// Builds the real search for this machine. Composes paths only -- nothing is created or probed.
    /// </summary>
    /// <param name="installRoot">The directory Jane.exe lives in.</param>
    public static OllamaSearchOptions ForMachine(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new OllamaSearchOptions
        {
            EnvironmentOverride = Environment.GetEnvironmentVariable(OllamaLocator.ExeEnvironmentVariable),
            InstallExe = Path.Combine(installRoot, "tools", "ollama", OllamaLocator.ExeName),
            UserExe = Path.Combine(OllamaLocator.UserRuntimeDirectory, OllamaLocator.ExeName),

            // Where the official Windows installer puts it. Reusing it saves the user a second
            // 1.4 GB copy of a binary they already have.
            SystemExes =
            [
                Path.Combine(localAppData, "Programs", "Ollama", OllamaLocator.ExeName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Ollama",
                    OllamaLocator.ExeName),
            ],
            PathDirectories = SplitPath(Environment.GetEnvironmentVariable("PATH")),
        };
    }

    private static IReadOnlyList<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Finds <c>ollama.exe</c>, in priority order, and says where it looked when it fails.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a directory walk upward from <c>AppContext.BaseDirectory</c> looking for
/// <c>tools\ollama</c>. That walk worked in a repository checkout and silently returned the base
/// directory in an installed build -- so on a real machine the supervised server was never
/// started, and the only symptom was two model rows reading "Not downloaded" forever.
/// </para>
/// <para>
/// Order is deliberate. An explicit override beats everything. The installer's own bundle beats a
/// copy Jane downloaded, because it is the one whose checksum the installer verified. Both beat a
/// user's existing Ollama, which Jane is a guest in rather than the owner of. <c>PATH</c> is last,
/// because anything found there is the least specified of the four.
/// </para>
/// </remarks>
public static class OllamaLocator
{
    /// <summary>Points Jane at a specific runtime. Diagnostics and support, not a user setting.</summary>
    public const string ExeEnvironmentVariable = "JANE_OLLAMA_EXE";

    public const string ExeName = "ollama.exe";

    /// <summary>
    /// Where Jane downloads its own runtime: <c>%LOCALAPPDATA%\Jane\tools\ollama</c>.
    /// </summary>
    /// <remarks>
    /// Under the profile rather than beside the executable, because Program Files is not writable
    /// by the user Jane runs as -- which is the whole reason the installer cannot simply fetch it
    /// after the fact, and why the in-app installer writes here instead.
    /// </remarks>
    public static string UserRuntimeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jane",
        "tools",
        "ollama");

    public static OllamaLocation Locate(OllamaSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> searched = [];
        List<string> notes = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        OllamaRuntime? Try(string? candidate, OllamaRuntimeSource source)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                return null;
            }

            searched.Add(candidate);
            return options.Exists(candidate) ? new OllamaRuntime(candidate, source) : null;
        }

        if (Try(options.EnvironmentOverride, OllamaRuntimeSource.EnvironmentOverride) is { } overridden)
        {
            return new OllamaLocation { Runtime = overridden, Searched = searched, Notes = notes };
        }

        // A stale variable must not break a working machine, so the search continues -- but
        // quietly using a different runtime than the one explicitly named is worse than either,
        // hence the note.
        if (!string.IsNullOrWhiteSpace(options.EnvironmentOverride))
        {
            notes.Add(
                $"{ExeEnvironmentVariable} is set to {options.EnvironmentOverride}, which does not exist. Ignoring it.");
        }

        var found =
            Try(options.InstallExe, OllamaRuntimeSource.InstallDirectory) ??
            Try(options.UserExe, OllamaRuntimeSource.UserProfile) ??
            options.SystemExes
                .Select(exe => Try(exe, OllamaRuntimeSource.SystemInstall))
                .FirstOrDefault(runtime => runtime is not null) ??
            options.PathDirectories
                .Select(directory => Try(Path.Combine(directory, ExeName), OllamaRuntimeSource.Path))
                .FirstOrDefault(runtime => runtime is not null);

        return new OllamaLocation { Runtime = found, Searched = searched, Notes = notes };
    }
}
