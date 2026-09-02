namespace Jane.App.Tests;

/// <summary>
/// Attribution is a licence obligation here, not documentation.
/// </summary>
/// <remarks>
/// Parakeet-TDT-0.6B-v2 is CC-BY-4.0 and Silero VAD is MIT; both require attribution in the
/// shipped product. A NOTICE.md that silently loses an entry is a licence violation, so the
/// entries are asserted rather than trusted to survive editing.
/// </remarks>
public sealed class NoticeTests
{
    private static string Notice => File.ReadAllText(PackagingTests.FindRepoFile("NOTICE.md"));

    [Theory]
    [InlineData("Parakeet-TDT-0.6B-v2", "CC-BY-4.0")]
    [InlineData("Silero VAD", "MIT")]
    [InlineData("Qwen3", "Apache License 2.0")]
    [InlineData("sherpa-onnx", "Apache-2.0")]
    [InlineData("Whisper", "MIT")]
    public void EveryShippedModelAndRuntimeIsAttributedWithItsLicence(string component, string licence)
    {
        var notice = Notice;

        Assert.Contains(component, notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(licence, notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoticeNamesTheCopyrightHolderForEveryAttributionRequiredLicence()
    {
        // CC-BY specifically requires naming the creator, not merely the licence.
        var notice = Notice;

        Assert.Contains("NVIDIA", notice, StringComparison.Ordinal);
        Assert.Contains("Silero Team", notice, StringComparison.Ordinal);
        Assert.Contains("creativecommons.org/licenses/by/4.0", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeStatesTheNetworkAndRetentionBehaviourPlainly()
    {
        // The plan requires the product to say plainly that history is plaintext and retained, and
        // that nothing leaves the machine on the dictation path. Saying it only in a design
        // document would not be saying it to the user.
        var notice = Notice;

        Assert.Contains("plaintext", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Audio is never written to disk", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loopback", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No telemetry", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishScriptShipsTheNoticeBesideTheBinary()
    {
        // The About view is not enough on its own: a copied build directory has to carry the
        // attribution with it.
        var publish = File.ReadAllText(PackagingTests.FindRepoFile(Path.Combine("build", "publish.ps1")));

        Assert.Contains("NOTICE.md", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningScriptConstrainsTheCertificateToCodeSigningOnly()
    {
        // A self-signed root in the machine's trust store that is valid for anything would let
        // whatever it signed be trusted for TLS, EFS, and everything else. The EKU OID is the
        // constraint, so it is asserted here rather than left to a reader of the script.
        var script = File.ReadAllText(PackagingTests.FindRepoFile(Path.Combine("build", "sign-uiaccess.ps1")));

        // 1.3.6.1.5.5.7.3.3 is Code Signing. 1.3.6.1.5.5.7.3.2 is Client Authentication, which is
        // what the first version of this script asked for, and Set-AuthenticodeSignature refused
        // the certificate outright: "The specified certificate is not suitable for code signing."
        Assert.Contains("1.3.6.1.5.5.7.3.3", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1.3.6.1.5.5.7.3.2", script, StringComparison.Ordinal);
        Assert.Contains("Export-PfxCertificate", script, StringComparison.Ordinal);

        // And the private key must be removed from the store after signing.
        Assert.Contains("Remove-Item -Force", script, StringComparison.Ordinal);
        Assert.Contains("KeepPrivateKey", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallScriptTargetsProgramFilesBecauseUiAccessRequiresIt()
    {
        var script = File.ReadAllText(PackagingTests.FindRepoFile(Path.Combine("build", "install.ps1")));

        Assert.Contains("ProgramFiles", script, StringComparison.Ordinal);
        Assert.Contains(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", script, StringComparison.Ordinal);

        // An unsigned install must degrade with an explanation rather than silently producing a
        // binary Windows will not launch.
        Assert.Contains("not validly signed", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishScriptSelectsTheUiAccessManifest()
    {
        var publish = File.ReadAllText(PackagingTests.FindRepoFile(Path.Combine("build", "publish.ps1")));

        Assert.Contains("-p:JaneUiAccess=true", publish, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", publish, StringComparison.Ordinal);
        Assert.Contains("win-x64", publish, StringComparison.Ordinal);
    }
}
