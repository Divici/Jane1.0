namespace Jane.Core.Platform;

/// <summary>
/// Every path Jane writes to, in one place.
/// </summary>
/// <remarks>
/// The root is overridable through <c>JANE_MODEL_DIR</c> and through the constructor so tests
/// never touch the real profile. Nothing here is created on construction -- probing "is the
/// model directory writable" must not be the thing that creates it.
/// </remarks>
public sealed class JanePaths
{
    public const string EnvModelDir = "JANE_MODEL_DIR";

    public JanePaths(string? root = null, string? modelDirOverride = null)
    {
        Root = root ?? Path.Combine(
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
