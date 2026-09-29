using System.Net.Sockets;

namespace Jane.Core.Models;

/// <summary>Why a model could not be made available. Every case has designed remedy text.</summary>
public enum ModelDownloadFailure
{
    /// <summary>No route to the host. The only network Jane ever needs, and only on demand.</summary>
    Offline,

    /// <summary>The server answered, but not with the file.</summary>
    HttpError,

    /// <summary>The bytes arrived and hashed to something else. Never unpacked.</summary>
    ChecksumMismatch,

    /// <summary>Ran out of room part-way through.</summary>
    DiskFull,

    /// <summary>The archive downloaded and verified but could not be unpacked.</summary>
    ExtractionFailed,

    /// <summary>
    /// Nothing was listening where a local service should have been.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="HttpError"/> because the two need opposite advice. An HTTP failure
    /// is a remote host that could not serve a file, and retrying is reasonable. A refused
    /// connection to loopback is a service on this machine that is not running, where retrying
    /// fails identically forever -- which is what onboarding did, reporting "the release asset may
    /// have moved" for an Ollama that had simply never been started.
    /// </remarks>
    ServiceUnreachable,
}

public sealed class ModelDownloadException(ModelDownloadFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ModelDownloadFailure Failure { get; } = failure;

    /// <summary>What the user can actually do about it, shown in onboarding and settings.</summary>
    public string Remedy => Failure switch
    {
        ModelDownloadFailure.Offline =>
            "Jane needs a network connection once, to download its models. Nothing else it does ever leaves this machine. Reconnect and retry -- a partial download resumes where it stopped.",
        ModelDownloadFailure.HttpError =>
            "The model host did not return the file. Retry; if it keeps failing the release asset may have moved, which is a Jane bug.",
        ModelDownloadFailure.ChecksumMismatch =>
            "The downloaded file does not match its pinned checksum, so it was discarded rather than used. This is usually a truncated or proxied download -- retry on a different network.",
        ModelDownloadFailure.DiskFull =>
            "There is not enough free space to finish. Free up a few GB, or point JANE_MODEL_DIR at a drive that has room.",
        ModelDownloadFailure.ExtractionFailed =>
            "The archive verified but could not be unpacked. Delete the model directory and retry.",
        ModelDownloadFailure.ServiceUnreachable =>
            "Jane's language-model service is not running, so there is nothing to pull the model into. Run build/get-ollama.ps1 to fetch the standalone ollama.exe -- Jane supervises its own copy on port 11435 and ignores any Ollama desktop app on 11434. Retrying the download alone will not help.",
        _ => "Retry the download.",
    };

    /// <summary>
    /// Wraps an exception the provisioner did not classify, working out what it actually was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything unrecognised used to become <see cref="ModelDownloadFailure.HttpError"/>, whose
    /// remedy says the release asset may have moved. Onboarding printed exactly that under a row
    /// whose own error text read <c>the target machine actively refused it (127.0.0.1:11435)</c> --
    /// Jane's own language-model service, on Jane's own port, never started. Sending somebody to
    /// retry a download that cannot succeed is worse than saying nothing, because it looks like an
    /// answer.
    /// </para>
    /// <para>
    /// Loopback is the distinguishing detail and it is checked through the socket error rather than
    /// the address, since the address is not always on the exception. A refusal means nothing is
    /// listening; a timeout or an unresolved host is the network, and the existing remedies already
    /// describe those correctly.
    /// </para>
    /// </remarks>
    public static ModelDownloadException FromUnclassified(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ModelDownloadException already)
        {
            return already;
        }

        var refused = exception is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            || exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };

        return new ModelDownloadException(
            refused ? ModelDownloadFailure.ServiceUnreachable : ModelDownloadFailure.HttpError,
            exception.Message,
            exception);
    }
}

/// <param name="BytesDownloaded">Includes bytes already on disk from a resumed partial download.</param>
public sealed record ModelDownloadProgress(string AssetId, long BytesDownloaded, long TotalBytes, string Stage)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp(BytesDownloaded / (double)TotalBytes, 0, 1);
}
