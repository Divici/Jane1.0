using System.Globalization;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Jane.Core.Models;

namespace Jane.Speech;

/// <summary>
/// Fetches pinned model weights, verifies them, and unpacks them atomically.
/// </summary>
/// <remarks>
/// This is the only outbound network in Jane, and it only runs when the user asks for it. Every
/// URL comes from <see cref="ModelCatalog"/>; nothing here accepts an arbitrary address.
///
/// Downloads resume. A 482 MB tarball over a flaky connection is otherwise a loop of starting
/// over, and onboarding is where a first-time user is least willing to wait twice.
/// </remarks>
public sealed class ModelDownloader(HttpClient http, string modelRoot)
{
    /// <summary>Partial downloads live here so a half-file is never mistaken for an install.</summary>
    private string StagingDirectory => Path.Combine(modelRoot, "_dl");

    public string ModelRoot => modelRoot;

    /// <summary>True when the asset is present, complete, and (for tarballs) has all its components.</summary>
    public bool IsInstalled(ModelAsset asset)
    {
        var target = asset.ResolvePath(modelRoot);

        if (asset.Kind == ModelArtifactKind.SingleFile)
        {
            return File.Exists(target) && new FileInfo(target).Length == asset.SizeBytes;
        }

        if (!Directory.Exists(target))
        {
            return false;
        }

        // A directory that exists but is missing a component is an interrupted extraction, not an
        // install. Treating it as installed is how a truncated model reaches inference.
        return asset.Id == ModelCatalog.ParakeetV2Int8.Id
            ? ModelCatalog.ParakeetComponents.All(c => File.Exists(Path.Combine(target, c)))
            : Directory.EnumerateFileSystemEntries(target).Any();
    }

    public async Task EnsureAsync(
        ModelAsset asset,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsInstalled(asset))
        {
            progress?.Report(new ModelDownloadProgress(asset.Id, asset.SizeBytes, asset.SizeBytes, "already installed"));
            return;
        }

        Directory.CreateDirectory(StagingDirectory);
        var staged = Path.Combine(StagingDirectory, asset.Id + Path.GetExtension(asset.Url.AbsolutePath));

        await DownloadAsync(asset, staged, progress, cancellationToken);
        await VerifyAsync(asset, staged, progress, cancellationToken);
        Install(asset, staged, progress);

        File.Delete(staged);
    }

    private async Task DownloadAsync(
        ModelAsset asset,
        string staged,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var alreadyHave = File.Exists(staged) ? new FileInfo(staged).Length : 0;

        if (alreadyHave == asset.SizeBytes)
        {
            return;
        }

        if (alreadyHave > asset.SizeBytes)
        {
            // Longer than expected means the pin moved or the file is not what we think. Start over.
            File.Delete(staged);
            alreadyHave = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (alreadyHave > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(alreadyHave, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Offline,
                $"Could not reach {asset.Url.Host} to download {asset.Id}.", ex);
        }

        using (response)
        {
            // A server that ignores Range answers 200 with the whole file; append would corrupt it.
            var resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resuming && alreadyHave > 0)
            {
                alreadyHave = 0;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelDownloadException(ModelDownloadFailure.HttpError,
                    $"{asset.Url} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                staged,
                resuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 << 20);

            var buffer = new byte[1 << 20];
            var written = alreadyHave;
            var lastReport = 0L;

            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    // Report every 4 MB: often enough for a smooth bar, rarely enough that the
                    // progress callback is not itself a cost on a 482 MB file.
                    if (written - lastReport >= 4 << 20)
                    {
                        lastReport = written;
                        progress?.Report(new ModelDownloadProgress(asset.Id, written, asset.SizeBytes, "downloading"));
                    }
                }
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new ModelDownloadException(ModelDownloadFailure.DiskFull,
                    $"Ran out of disk space downloading {asset.Id}.", ex);
            }

            progress?.Report(new ModelDownloadProgress(asset.Id, written, asset.SizeBytes, "downloaded"));
        }
    }

    private static async Task VerifyAsync(
        ModelAsset asset,
        string staged,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(asset.Sha256))
        {
            // A catalogue entry with no pin is a deliberate gap, not an oversight -- see the
            // WhisperQuants remark. Size is still checked so a truncated file is caught.
            var length = new FileInfo(staged).Length;
            if (asset.SizeBytes > 0 && length != asset.SizeBytes)
            {
                File.Delete(staged);
                throw new ModelDownloadException(ModelDownloadFailure.ChecksumMismatch,
                    $"{asset.Id} downloaded {length} bytes, expected {asset.SizeBytes}.");
            }

            return;
        }

        progress?.Report(new ModelDownloadProgress(asset.Id, asset.SizeBytes, asset.SizeBytes, "verifying"));

        await using var stream = File.OpenRead(staged);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Delete rather than keep: a resumable partial is useful, a wrong-hash complete file
            // is not, and leaving it would make the next attempt "resume" straight back into it.
            await stream.DisposeAsync();
            File.Delete(staged);
            throw new ModelDownloadException(ModelDownloadFailure.ChecksumMismatch,
                $"{asset.Id} hashed to {actual}, expected {asset.Sha256}.");
        }
    }

    private void Install(ModelAsset asset, string staged, IProgress<ModelDownloadProgress>? progress)
    {
        progress?.Report(new ModelDownloadProgress(asset.Id, asset.SizeBytes, asset.SizeBytes, "installing"));

        var target = asset.ResolvePath(modelRoot);
        Directory.CreateDirectory(modelRoot);

        if (asset.Kind == ModelArtifactKind.SingleFile)
        {
            // Move, not copy: the staged file is already verified, and a move is atomic on the
            // same volume so the target is never observed half-written.
            File.Move(staged, target, overwrite: true);
            return;
        }

        // Extract beside the target, then swap. An interrupted extraction leaves a temp directory
        // to clean up rather than a half-populated model directory that IsInstalled would accept.
        var temp = target + ".incoming";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        try
        {
            Directory.CreateDirectory(temp);
            using (var file = File.OpenRead(staged))
            using (var bzip2 = new BZip2InputStream(file))
            using (var tar = TarArchive.CreateInputTarArchive(bzip2, System.Text.Encoding.UTF8))
            {
                tar.ExtractContents(temp);
            }

            // sherpa-onnx tarballs contain a single top-level directory; lift its contents up so
            // RelativePath means what the catalogue says it means.
            var entries = Directory.GetFileSystemEntries(temp);
            var root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : temp;

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(root, target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BZip2Exception or TarException)
        {
            throw new ModelDownloadException(ModelDownloadFailure.ExtractionFailed,
                $"Could not unpack {asset.Id}: {ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static bool IsDiskFull(IOException ex)
    {
        // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL, as HRESULTs.
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);
        return ex.HResult is DiskFull or HandleDiskFull;
    }

    public string Describe(ModelAsset asset) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{asset.Id} ({asset.SizeDescription}, {asset.License}) -> {asset.ResolvePath(modelRoot)}");
}
