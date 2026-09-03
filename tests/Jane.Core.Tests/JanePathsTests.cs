using Jane.Core.Platform;

namespace Jane.Core.Tests;

/// <summary>
/// Where Jane writes, and how a test keeps out of the real profile.
/// </summary>
/// <remarks>
/// <see cref="JanePaths"/> has always claimed in its own documentation that the root is
/// overridable "so tests never touch the real profile", and for a test running in-process that was
/// true: pass a root to the constructor. It was not true for the one test that launches Jane as a
/// child process to measure its idle footprint. That process constructs its own
/// <see cref="JanePaths"/> with no arguments, so it opened the database in
/// <c>%LOCALAPPDATA%\Jane</c> -- the real one, belonging to whoever ran the suite, alongside their
/// running copy of Jane.
/// <para>
/// <c>JANE_HOME</c> closes that gap, because an environment variable is the only kind of override
/// that survives a process boundary.
/// </para>
/// </remarks>
public sealed class JanePathsTests
{
    [Fact]
    public void TheHomeOverrideKeepsEverythingOutOfTheRealProfile()
    {
        var home = Path.Combine(Path.GetTempPath(), "jane-paths-tests", Guid.NewGuid().ToString("N"));

        var paths = RunWith(JanePaths.EnvHome, home, () => new JanePaths());

        Assert.Equal(home, paths.Root);
        Assert.StartsWith(home, paths.Database, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(home, paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(home, paths.Models, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExplicitRootStillWinsOverTheEnvironment()
    {
        // The constructor argument is what in-process tests use, and it has to keep working even
        // when the variable is set -- otherwise one test's environment leaks into another's paths.
        var fromEnvironment = Path.Combine(Path.GetTempPath(), "jane-paths-tests", "environment");
        var explicitRoot = Path.Combine(Path.GetTempPath(), "jane-paths-tests", "explicit");

        var paths = RunWith(JanePaths.EnvHome, fromEnvironment, () => new JanePaths(explicitRoot));

        Assert.Equal(explicitRoot, paths.Root);
    }

    [Fact]
    public void WithNoOverrideAtAllJaneLivesInTheLocalApplicationDataFolder()
    {
        var paths = RunWith(JanePaths.EnvHome, null, () => new JanePaths());

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jane");

        Assert.Equal(expected, paths.Root);
    }

    [Fact]
    public void TheModelDirectoryCanStillBeMovedOnItsOwn()
    {
        // Models are gigabytes and the reason JANE_MODEL_DIR exists is to put them on another
        // drive. That has to stay independent of where the database lives.
        var home = Path.Combine(Path.GetTempPath(), "jane-paths-tests", "home");
        var models = Path.Combine(Path.GetTempPath(), "jane-paths-tests", "models");

        var paths = RunWith(
            JanePaths.EnvHome, home,
            () => RunWith(JanePaths.EnvModelDir, models, () => new JanePaths()));

        Assert.Equal(home, paths.Root);
        Assert.Equal(models, paths.Models);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with one environment variable set, then puts it back.
    /// </summary>
    /// <remarks>
    /// Environment variables are process-wide, and xUnit runs tests in parallel, so this is only
    /// safe because <see cref="JanePaths"/> reads them synchronously during construction and
    /// nothing else in this assembly reads these two.
    /// </remarks>
    private static T RunWith<T>(string name, string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
