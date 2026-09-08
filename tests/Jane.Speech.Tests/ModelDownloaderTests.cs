using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jane.Core.Models;
using Jane.Speech;

namespace Jane.Speech.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-downloader-test");

    [Fact]
    public async Task VerifiedDownload_LandsAtTheCataloguedPath()
    {
        var payload = Encoding.UTF8.GetBytes("pretend this is an onnx graph");
        var asset = SingleFileAsset(payload, "model.onnx");
        var handler = new StubHandler(payload);
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        await downloader.EnsureAsync(asset, progress: null, TestContext.Current.CancellationToken);

        var path = asset.ResolvePath(_root.FullName);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(downloader.IsInstalled(asset));
    }

    [Fact]
    public async Task ChecksumMismatch_RaisesATypedErrorAndInstallsNothing()
    {
        // The failure this prevents is a corrupted 482 MB encoder reaching inference and failing
        // as an unreadable ONNX graph during the user's first dictation.
        var payload = Encoding.UTF8.GetBytes("truncated");
        var asset = SingleFileAsset(payload, "model.onnx") with { Sha256 = new string('a', 64) };
        var handler = new StubHandler(payload);
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.ChecksumMismatch, ex.Failure);
        Assert.False(File.Exists(asset.ResolvePath(_root.FullName)));
        Assert.False(string.IsNullOrWhiteSpace(ex.Remedy));

        // The bad bytes must not be left behind, or the next attempt would "resume" into them.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.FullName, "_dl")));
    }

    [Fact]
    public async Task InterruptedDownload_ResumesFromWhereItStopped()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        var asset = SingleFileAsset(payload, "model.onnx");

        // Simulate a previous attempt that stopped a third of the way through.
        var staging = Path.Combine(_root.FullName, "_dl");
        Directory.CreateDirectory(staging);
        var partial = Path.Combine(staging, asset.Id + ".onnx");
        await File.WriteAllBytesAsync(partial, payload[..1400], TestContext.Current.CancellationToken);

        var handler = new StubHandler(payload) { HonourRange = true };
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        await downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken);

        Assert.Equal(1400, handler.LastRangeFrom);
        Assert.Equal(payload, await File.ReadAllBytesAsync(asset.ResolvePath(_root.FullName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ServerIgnoringRange_RestartsRatherThanCorruptingTheFile()
    {
        // A proxy that answers 200 to a Range request would otherwise have its full response
        // appended to the partial file, producing a longer-than-expected, wrong-hash download.
        var payload = Encoding.UTF8.GetBytes(new string('y', 2048));
        var asset = SingleFileAsset(payload, "model.onnx");

        var staging = Path.Combine(_root.FullName, "_dl");
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, asset.Id + ".onnx"), payload[..500], TestContext.Current.CancellationToken);

        var handler = new StubHandler(payload) { HonourRange = false };
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        await downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(asset.ResolvePath(_root.FullName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_RaisesATypedErrorWithAnActionableRemedy()
    {
        var asset = SingleFileAsset(Encoding.UTF8.GetBytes("x"), "model.onnx");
        var handler = new StubHandler([]) { Throw = new HttpRequestException("no such host") };
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.Offline, ex.Failure);
        Assert.Contains("network", ex.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpError_IsDistinguishedFromBeingOffline()
    {
        var asset = SingleFileAsset(Encoding.UTF8.GetBytes("x"), "model.onnx");
        var handler = new StubHandler([]) { Status = HttpStatusCode.NotFound };
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.HttpError, ex.Failure);
    }

    [Fact]
    public async Task AlreadyInstalled_MakesNoRequest()
    {
        var payload = Encoding.UTF8.GetBytes("already here");
        var asset = SingleFileAsset(payload, "model.onnx");
        await File.WriteAllBytesAsync(asset.ResolvePath(_root.FullName), payload, TestContext.Current.CancellationToken);

        var handler = new StubHandler(payload);
        var downloader = new ModelDownloader(handler.CreateClient(), _root.FullName);

        await downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void IncompleteExtraction_IsNotMistakenForAnInstall()
    {
        // A directory with some but not all components is an interrupted extraction. Treating it
        // as installed is how a truncated model reaches inference.
        var directory = ModelCatalog.ParakeetV2Int8.ResolvePath(_root.FullName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tokens.txt"), "a");

        var downloader = new ModelDownloader(new HttpClient(), _root.FullName);

        Assert.False(downloader.IsInstalled(ModelCatalog.ParakeetV2Int8));

        foreach (var component in ModelCatalog.ParakeetComponents)
        {
            File.WriteAllText(Path.Combine(directory, component), "x");
        }

        Assert.True(downloader.IsInstalled(ModelCatalog.ParakeetV2Int8));
    }

    [Fact]
    public void ProgressFraction_IsClampedAndSafeForAZeroTotal()
    {
        Assert.Equal(0.5, new ModelDownloadProgress("a", 50, 100, "downloading").Fraction);
        Assert.Equal(0, new ModelDownloadProgress("a", 50, 0, "downloading").Fraction);
        Assert.Equal(1, new ModelDownloadProgress("a", 500, 100, "downloading").Fraction);
    }

    private static ModelAsset SingleFileAsset(byte[] payload, string fileName) => new(
        Id: "test-asset",
        Url: new Uri("https://example.invalid/" + fileName),
        Sha256: Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        SizeBytes: payload.Length,
        Kind: ModelArtifactKind.SingleFile,
        RelativePath: fileName,
        License: "MIT",
        Attribution: "test");

    public void Dispose() => _root.Delete(recursive: true);

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public long LastRangeFrom { get; private set; } = -1;

        public bool HonourRange { get; init; } = true;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Exception? Throw { get; init; }

        public HttpClient CreateClient() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (Throw is not null)
            {
                throw Throw;
            }

            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            LastRangeFrom = from ?? -1;

            if (from is { } offset && HonourRange)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload[(int)offset..]),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
