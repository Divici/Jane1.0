using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Jane.Core.Models;

namespace Jane.Llm;

/// <summary>
/// The Ollama release Jane runs against.
/// </summary>
/// <remarks>
/// Pinned, and bumped deliberately. Jane does not use the winget desktop package: that installs a
/// tray service which owns port 11434, autostarts with Windows and updates itself over the network,
/// and all three break Jane's supervision model and its zero-egress promise. The release archive is
/// a plain <c>ollama.exe</c> plus runner DLLs, which Jane starts as a supervised child on a
/// non-default port.
/// <para>
/// The hash is the one <c>build/get-ollama.ps1</c> recorded when it actually downloaded this
/// version, and a test pins the two together so they cannot drift apart.
/// </para>
/// </remarks>
public static class OllamaRelease
{
    public const string Version = "v0.33.2";

    public const string AssetName = "ollama-windows-amd64.zip";

    /// <summary>SHA-256 of <see cref="AssetName"/> at <see cref="Version"/>.</summary>
    public const string Sha256 = "2439cbea65310b1aadf7d8fc41d7faf5d033f920d42e00a476c58bf9bff6950e";

    public static Uri DownloadUrl { get; } =
        new($"https://github.com/ollama/ollama/releases/download/{Version}/{AssetName}");
}

/// <summary>
/// Downloads and unpacks the model runtime, from inside Jane.
/// </summary>
/// <remarks>
/// <para>
/// Does what <c>build/get-ollama.ps1</c> does, for a user who has an installed Jane and no
/// repository checkout: fetch the pinned release archive, verify it against the pinned hash, and
/// unpack it. That user previously had no way at all to obtain the runtime, and no message saying
/// one was needed -- the two language-model rows read "Not downloaded" and stayed that way.
/// </para>
/// <para>
/// The destination is under the user profile rather than beside the executable, because Jane
/// installs into Program Files -- a "secure location" is a uiAccess requirement, not a preference
/// -- and the user Jane runs as cannot write there. That is also why the installer script cannot
/// simply fetch the runtime after the fact.
/// </para>
/// <para>
/// Nothing is unpacked before the hash matches, and the destination is emptied before extraction:
/// a stale DLL from an older release mixed in with a newer executable fails at model-load time,
/// hours after the thing that caused it.
/// </para>
/// </remarks>
public sealed class OllamaRuntimeInstaller
{
    private readonly HttpClient _http;
    private readonly string _expectedSha256;
    private readonly Uri _url;

    public OllamaRuntimeInstaller(
        HttpClient http,
        string destination,
        string? expectedSha256 = null,
        Uri? url = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        _http = http;
        _expectedSha256 = expectedSha256 ?? OllamaRelease.Sha256;
        _url = url ?? OllamaRelease.DownloadUrl;
        Destination = destination;
    }

    public string Destination { get; }

    public string ExePath => Path.Combine(Destination, OllamaLocator.ExeName);

    private string VersionStamp => Path.Combine(Destination, ".version");

    private string HashStamp => Path.Combine(Destination, ".sha256");

    /// <summary>Whether this exact release is already unpacked here.</summary>
    public bool IsInstalled =>
        File.Exists(ExePath) &&
        File.Exists(VersionStamp) &&
        string.Equals(ReadStamp(VersionStamp), OllamaRelease.Version, StringComparison.Ordinal);

    /// <summary>
    /// Fetches and unpacks the runtime, or returns immediately if this version is already here.
    /// </summary>
    /// <param name="force">Re-downloads even when the version stamp already matches.</param>
    public async Task InstallAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (IsInstalled && !force)
        {
            progress?.Report(Step(1, 1, "installed"));
            return;
        }

        var staging = Path.Combine(
            Path.GetTempPath(), "jane-ollama-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, OllamaRelease.AssetName);

        try
        {
            await DownloadAsync(archive, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(Step(0, 1, "verifying"));
            var actual = await HashAsync(archive, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDownloadException(
                    ModelDownloadFailure.ChecksumMismatch,
                    $"{OllamaRelease.AssetName} hashed to {actual}, not the pinned {_expectedSha256}.");
            }

            progress?.Report(Step(0, 1, "extracting"));
            Extract(archive, staging, actual);

            progress?.Report(Step(1, 1, "installed"));
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task DownloadAsync(
        string archive, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelDownloadException(
                    ModelDownloadFailure.HttpError,
                    $"{_url} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var total = response.Content.Headers.ContentLength ?? 0;

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(archive);

            var buffer = new byte[81920];
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                progress?.Report(Step(written, total, "downloading"));
            }

            // A zero-length body that still returned 200 is a proxy or a CDN edge misbehaving. It
            // would otherwise fail later as a checksum mismatch, whose remedy is the wrong advice.
            if (written == 0)
            {
                throw new ModelDownloadException(
                    ModelDownloadFailure.HttpError, $"{_url} returned an empty response body.");
            }
        }
        catch (ModelDownloadException)
        {
            throw;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.DiskFull,
                "There is not enough free space to download the model runtime.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.Offline,
                $"Could not download {_url}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Unpacks into a scratch directory first, then swaps it in.
    /// </summary>
    /// <remarks>
    /// Extracting straight into the destination would leave it unusable if the archive turned out
    /// not to contain the executable -- half-unpacked, but present, which the locator would find
    /// and the supervisor would then fail to start.
    /// </remarks>
    private void Extract(string archive, string staging, string hash)
    {
        var unpacked = Path.Combine(staging, "unpacked");

        try
        {
            ZipFile.ExtractToDirectory(archive, unpacked);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.ExtractionFailed,
                $"{OllamaRelease.AssetName} verified but could not be unpacked: {ex.Message}",
                ex);
        }

        if (!File.Exists(Path.Combine(unpacked, OllamaLocator.ExeName)))
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.ExtractionFailed,
                $"{OllamaRelease.AssetName} unpacked without {OllamaLocator.ExeName}. The upstream asset layout has changed, which is a Jane bug.");
        }

        try
        {
            if (Directory.Exists(Destination))
            {
                Directory.Delete(Destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Destination.TrimEnd(Path.DirectorySeparatorChar))!);
            Directory.Move(unpacked, Destination);

            File.WriteAllText(VersionStamp, OllamaRelease.Version);
            File.WriteAllText(HashStamp, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.ExtractionFailed,
                $"Could not put the unpacked runtime into {Destination}: {ex.Message}",
                ex);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ModelDownloadProgress Step(long completed, long total, string stage) =>
        new(OllamaRelease.AssetName, completed, total <= 0 ? completed : total, stage);

    private static string ReadStamp(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim().TrimStart('﻿');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, as HResults.</summary>
    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x27 or 0x70;

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is untidy, not a failure, and this runs in a finally.
        }
    }
}
