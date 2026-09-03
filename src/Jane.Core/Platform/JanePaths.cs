namespace Jane.Core.Platform;

/// <summary>
/// Every path Jane writes to, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The root is overridable two ways, and it needs both. The constructor argument is what an
/// in-process test uses. <c>JANE_HOME</c> is what a test that <em>launches Jane</em> uses -- the
/// idle-footprint measurement starts the real app as a child process, and a child constructs its
/// own <see cref="JanePaths"/> with no arguments, so without an environment variable it opens the
/// database belonging to whoever ran the suite, next to their running copy of Jane.
/// </para>
/// <para>
/// <c>JANE_MODEL_DIR</c> is separate and stays separate: model weights are gigabytes, and putting
/// them on another drive has nothing to do with where the database lives.
/// </para>
/// <para>
/// Nothing here is created on construction -- probing "is the model directory writable" must not
/// be the thing that creates it.
/// </para>
/// </remarks>
public sealed class JanePaths
{
    public const string EnvModelDir = "JANE_MODEL_DIR";

    /// <summary>Moves everything Jane writes. The only override that crosses a process boundary.</summary>
    public const string EnvHome = "JANE_HOME";

    public JanePaths(string? root = null, string? modelDirOverride = null)
    {
        var envHome = Environment.GetEnvironmentVariable(EnvHome);

        Root = root
            ?? (string.IsNullOrWhiteSpace(envHome) ? null : Path.GetFullPath(envHome))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jane");

        var envModelDir = modelDirOverride ?? Environment.GetEnvironmentVariable(EnvModelDir);
        Models = string.IsNullOrWhiteSpace(envModelDir)
            ? Path.Combine(Root, "models")
            : Path.GetFullPath(envModelDir);
    }

    /// <summary>%LOCALAPPDATA%\Jane</summary>
    public string Root { get; }

    /// <summary>Where ASR/VAD weights land. Overridable so a second drive can hold them.</summary>
    public string Models { get; }

    /// <summary>Phase 1 settings file, migrated into <see cref="Database"/> at Phase 10.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Transcript history and, from Phase 10, settings.</summary>
    public string Database => Path.Combine(Root, "jane.db");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Where `doctor`, `bench` and `eval` drop their reports.</summary>
    public string Reports => Path.Combine(Root, "reports");
}
