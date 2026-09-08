using System.Net.Sockets;
using Jane.Core.Models;
using Jane.Speech;

namespace Jane.Speech.Tests;

/// <summary>
/// Telling the user which thing broke.
/// </summary>
/// <remarks>
/// <para>
/// Onboarding reported both language-model rows as <c>ModelDownloadFailure.HttpError</c>, whose
/// remedy reads "the release asset may have moved, which is a Jane bug." The actual error, printed
/// on the same row, was <c>No connection could be made because the target machine actively refused
/// it. (127.0.0.1:11435)</c> -- Jane's own Ollama, on Jane's own port, not running.
/// </para>
/// <para>
/// Those are opposite problems. One is a remote host that cannot serve a file and can only be
/// retried; the other is a local service the user can start, where retrying the download will fail
/// identically forever. Sending somebody to retry a download that cannot succeed is worse than
/// saying nothing, because it looks like an answer.
/// </para>
/// <para>
/// So a refused connection to loopback is classified apart from an HTTP failure. Loopback matters:
/// the same refusal against a remote host really is the model host being down, and the retry
/// advice is right there.
/// </para>
/// </remarks>
public sealed class ModelFailureClassificationTests
{
    [Fact]
    public void ARefusedLoopbackConnectionIsTheLocalServiceBeingDown()
    {
        var refused = new HttpRequestException(
            "No connection could be made because the target machine actively refused it. (127.0.0.1:11435)",
            new SocketException((int)SocketError.ConnectionRefused));

        var classified = ModelDownloadException.FromUnclassified(refused);

        Assert.Equal(ModelDownloadFailure.ServiceUnreachable, classified.Failure);
    }

    [Fact]
    public void TheRemedyForAMissingServiceNamesTheScriptThatInstallsIt()
    {
        var classified = ModelDownloadException.FromUnclassified(
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));

        // The whole point of splitting this case out. "Retry" is not an action here.
        Assert.Contains("get-ollama.ps1", classified.Remedy);
        Assert.DoesNotContain("release asset", classified.Remedy);
    }

    [Fact]
    public void ABareSocketRefusalIsClassifiedToo()
    {
        // The provisioner does not always wrap. A raw SocketException has to land in the same place
        // as the wrapped one, or the classification depends on which layer happened to throw.
        var classified = ModelDownloadException.FromUnclassified(
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(ModelDownloadFailure.ServiceUnreachable, classified.Failure);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TimedOut)]
    public void OtherSocketFailuresAreStillOrdinaryNetworkTrouble(SocketError error)
    {
        // Only a refusal means "nothing is listening there". The rest are the network, and the
        // existing offline and HTTP remedies already say the right thing about them.
        var classified = ModelDownloadException.FromUnclassified(
            new HttpRequestException("boom", new SocketException((int)error)));

        Assert.NotEqual(ModelDownloadFailure.ServiceUnreachable, classified.Failure);
    }

    [Fact]
    public void AnUnrelatedFailureStaysAnHttpError()
    {
        var classified = ModelDownloadException.FromUnclassified(new InvalidOperationException("nope"));

        Assert.Equal(ModelDownloadFailure.HttpError, classified.Failure);
    }

    [Fact]
    public void TheOriginalExceptionIsKeptAsTheInnerOne()
    {
        var original = new InvalidOperationException("nope");

        var classified = ModelDownloadException.FromUnclassified(original);

        // The row shows the message; a log or a debugger still needs the thing that actually threw.
        Assert.Same(original, classified.InnerException);
        Assert.Equal("nope", classified.Message);
    }

    [Fact]
    public void AnAlreadyClassifiedFailureIsNotReclassified()
    {
        var already = new ModelDownloadException(ModelDownloadFailure.ChecksumMismatch, "bad hash");

        Assert.Same(already, ModelDownloadException.FromUnclassified(already));
    }
}
