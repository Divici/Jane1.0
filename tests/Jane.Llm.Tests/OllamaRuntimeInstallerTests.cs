using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jane.Core.Models;
using Jane.Llm;

namespace Jane.Llm.Tests;

/// <summary>
/// Fetching the model runtime from inside Jane, so a missing one is a button rather than homework.
/// </summary>
/// <remarks>
/// <para>
/// The runtime was previously only obtainable by running <c>build/get-ollama.ps1</c> from a
/// repository checkout. A user who installed Jane from the published artifact had no checkout, no
/// script, and no message telling them either existed -- the two language-model rows simply read
/// "Not downloaded" and the button did nothing that helped.
/// </para>
/// <para>
/// This does exactly what the script does, in the same order and with the same pinned version and
/// checksum, into the profile directory rather than beside the executable -- Program Files is not
/// writable by the user Jane runs as, which is why the installer cannot simply fetch it later and
/// why this exists at all.
/// </para>
/// </remarks>
public sealed class OllamaRuntimeInstallerTests
{
    [Fact]
    public async Task AVerifiedArchiveIsUnpackedAndStamped()
    {
        using var temp = new TempFolder();
        var archive = ZipContaining(("ollama.exe", "binary"), ("lib/runner.dll", "runner"));

        var installer = NewInstaller(temp, archive, Sha256Of(archive));
        await installer.InstallAsync(null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(temp.Path, "ollama.exe")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "lib", "runner.dll")));
        Assert.Equal(OllamaRelease.Version, File.ReadAllText(Path.Combine(temp.Path, ".version")).Trim());
        Assert.Equal(
            Sha256Of(archive),
            File.ReadAllText(Path.Combine(temp.Path, ".sha256")).Trim(),
            ignoreCase: true);
    }

    [Fact]
    public async Task ProgressIsReportedWhileDownloadingAndWhileUnpacking()
    {
        // A 1.4 GB download with no bar is indistinguishable from a hang, which is the specific
        // complaint that made "the Download button does nothing" the way this was reported.
        using var temp = new TempFolder();
        var archive = ZipContaining(("ollama.exe", new string('x', 40_000)));
        var steps = new List<ModelDownloadProgress>();

        var installer = NewInstaller(temp, archive, Sha256Of(archive));
        await installer.InstallAsync(
            new Progress<ModelDownloadProgress>(steps.Add), TestContext.Current.CancellationToken);

        Assert.Contains(steps, s => s.Stage == "downloading");
        Assert.Contains(steps, s => s.Stage == "verifying");
        Assert.Contains(steps, s => s.Stage == "extracting");
        Assert.Equal(1, steps[^1].Fraction);
    }

    [Fact]
    public async Task AnArchiveThatHashesToSomethingElseIsNeverUnpacked()
    {
        using var temp = new TempFolder();
        var archive = ZipContaining(("ollama.exe", "binary"));

        var installer = NewInstaller(temp, archive, expectedSha256: new string('a', 64));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => installer.InstallAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.ChecksumMismatch, ex.Failure);
        Assert.False(File.Exists(Path.Combine(temp.Path, "ollama.exe")));
    }

    [Fact]
    public async Task AnArchiveWithoutTheExecutableIsRejectedRatherThanLeftHalfInstalled()
    {
        // The upstream asset layout changing must not leave a directory that looks installed and
        // is not -- the locator would then find nothing and report a path that exists.
        using var temp = new TempFolder();
        var archive = ZipContaining(("readme.txt", "nothing useful"));

        var installer = NewInstaller(temp, archive, Sha256Of(archive));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => installer.InstallAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.ExtractionFailed, ex.Failure);
        Assert.False(File.Exists(Path.Combine(temp.Path, "readme.txt")));
    }

    [Fact]
    public async Task BeingOfflineSaysSoRatherThanBlamingTheReleaseAsset()
    {
        using var temp = new TempFolder();
        var installer = new OllamaRuntimeInstaller(
            new HttpClient(new ThrowingHandler(new HttpRequestException("No such host is known."))),
            temp.Path);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => installer.InstallAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.Offline, ex.Failure);
        Assert.Contains("network connection", ex.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AServerThatAnswersWithAnErrorIsAnHttpFailure()
    {
        using var temp = new TempFolder();
        var installer = new OllamaRuntimeInstaller(
            new HttpClient(new StatusHandler(HttpStatusCode.NotFound)), temp.Path);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => installer.InstallAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.HttpError, ex.Failure);
    }

    [Fact]
    public async Task AnExistingInstallOfTheSameVersionIsLeftAloneUnlessForced()
    {
        // Idempotent, like the script. Re-running must not spend 1.4 GB of somebody's data plan
        // to arrive at the file already sitting there.
        using var temp = new TempFolder();
        var archive = ZipContaining(("ollama.exe", "binary"));
        var handler = new ArchiveHandler(archive);

        var installer = new OllamaRuntimeInstaller(new HttpClient(handler), temp.Path, Sha256Of(archive));
        await installer.InstallAsync(null, TestContext.Current.CancellationToken);
        await installer.InstallAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Requests);
        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public async Task AHalfWrittenPreviousAttemptIsReplacedRatherThanMergedInto()
    {
        // The destination is emptied before extraction. A stale DLL from an older release mixed
        // in with a newer exe is the kind of thing that fails at model-load time, hours later.
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "stale.dll"), "from an older release");

        var archive = ZipContaining(("ollama.exe", "binary"));
        await NewInstaller(temp, archive, Sha256Of(archive))
            .InstallAsync(null, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(temp.Path, "stale.dll")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "ollama.exe")));
    }

    [Fact]
    public void ThePinnedReleaseMatchesWhatTheBuildScriptFetches()
    {
        // Two places would drift. The script is the one that has actually been run against the
        // network, so its recorded version and hash are the source of truth here.
        var scriptRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(scriptRoot, "build", "get-ollama.ps1"));

        Assert.Contains($"'{OllamaRelease.Version}'", script, StringComparison.Ordinal);
        Assert.Contains(OllamaRelease.AssetName, script, StringComparison.Ordinal);
        Assert.Equal(64, OllamaRelease.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", OllamaRelease.Sha256);
    }

    private static OllamaRuntimeInstaller NewInstaller(TempFolder temp, byte[] archive, string expectedSha256) =>
        new(new HttpClient(new ArchiveHandler(archive)), temp.Path, expectedSha256);

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ZipContaining(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jane.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Jane.sln not found above the test binary.");
    }

    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new ByteArrayContent(archive);
            content.Headers.ContentLength = archive.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "jane-runtime-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
