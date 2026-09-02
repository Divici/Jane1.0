using Jane.Core.Diagnostics;
using Jane.Core.Diagnostics.Probes;
using Jane.Core.Platform;

namespace Jane.Core.Tests;

public sealed class ProbeTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("jane-probe-test");

    [Fact]
    public async Task ModelDirectoryProbe_CreatesTheDirectoryAndConfirmsItIsWritable()
    {
        var models = Path.Combine(_temp.FullName, "models");
        var probe = new ModelDirectoryProbe(new JanePaths(_temp.FullName, models));

        var result = await probe.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Pass, result.Status);
        Assert.True(Directory.Exists(models));

        // The write canary must not survive the probe -- a stray file in the model directory
        // would confuse the downloader's resume logic.
        Assert.Empty(Directory.GetFiles(models));
    }

    [Fact]
    public async Task ModelDirectoryProbe_FailsWithARemedyWhenThePathIsAFile()
    {
        // A file where a directory should be is the shape a half-finished download leaves behind.
        var collision = Path.Combine(_temp.FullName, "models");
        await File.WriteAllTextAsync(collision, "not a directory", TestContext.Current.CancellationToken);

        var probe = new ModelDirectoryProbe(new JanePaths(_temp.FullName, collision));

        var result = await probe.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Fail, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
        Assert.Contains(JanePaths.EnvModelDir, result.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiskSpaceProbe_PassesWhenTheRequirementIsTrivial()
    {
        var probe = new DiskSpaceProbe(new JanePaths(_temp.FullName, _temp.FullName), requiredBytes: 1);

        var result = await probe.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Pass, result.Status);
        Assert.True(long.Parse(result.Data!["free_bytes"], System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task DiskSpaceProbe_FailsWithARemedyWhenTheRequirementCannotBeMet()
    {
        var probe = new DiskSpaceProbe(new JanePaths(_temp.FullName, _temp.FullName), requiredBytes: long.MaxValue);

        var result = await probe.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Fail, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
    }

    [Fact]
    public void JanePaths_HonoursTheModelDirectoryOverride()
    {
        var paths = new JanePaths(@"C:\jane-root", @"D:\weights");

        Assert.Equal(@"D:\weights", paths.Models);
        Assert.Equal(@"C:\jane-root\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\jane-root\jane.db", paths.Database);
    }

    [Fact]
    public void JanePaths_DefaultsModelsUnderTheRoot()
    {
        var paths = new JanePaths(@"C:\jane-root");

        Assert.Equal(Path.Combine(@"C:\jane-root", "models"), paths.Models);
    }

    [Fact]
    public void JanePaths_CreatesNothingOnConstruction()
    {
        // Probing "is the model directory writable" must not be the thing that creates it --
        // otherwise the probe can never report the interesting failure.
        var root = Path.Combine(_temp.FullName, "untouched");

        _ = new JanePaths(root);

        Assert.False(Directory.Exists(root));
    }

    public void Dispose() => _temp.Delete(recursive: true);
}
