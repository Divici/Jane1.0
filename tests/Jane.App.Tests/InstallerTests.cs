using System.Text.RegularExpressions;
using Jane.App.Composition;
using Jane.App.Tray;

namespace Jane.App.Tests;

/// <summary>
/// The installer somebody downloads from the README, checked as text.
/// </summary>
/// <remarks>
/// <para>
/// Installing Jane used to mean cloning the repository, building it, and running three PowerShell
/// scripts from an elevated prompt. <c>JaneSetup.exe</c> replaces all of that for anybody who is
/// not a developer, which makes it the first thing a new user ever runs -- and none of it is
/// compiled by the C# build, so nothing else would notice it drifting.
/// </para>
/// <para>
/// These are deliberately checks on the scripts rather than on a built installer. Building one
/// needs Inno Setup and several minutes; the release workflow does that, and then installs and
/// uninstalls the result on a clean machine. What is pinned here is the set of decisions that
/// would be silently wrong rather than loudly broken.
/// </para>
/// </remarks>
public sealed partial class InstallerTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(PackagingTests.FindRepoFile(Path.Combine(parts)));

    private static string SetupScript => Read("build", "installer", "Jane.iss");

    private static string MakeSetup => Read("build", "make-setup.ps1");

    private static string Workflow => Read(".github", "workflows", "release.yml");

    [Fact]
    public void ThePublicBuildDoesNotAskForUiAccess()
    {
        // The whole reason the public installer can exist. Windows refuses to START a binary whose
        // manifest asks for uiAccess unless it is signed by a certificate the machine trusts, and
        // the public build is unsigned. Shipping the uiAccess manifest would produce an installer
        // that succeeds and an application that never opens.
        Assert.Contains("-NoUiAccess", MakeSetup, StringComparison.Ordinal);

        var publish = Read("build", "publish.ps1");
        Assert.Contains("[switch]$NoUiAccess", publish, StringComparison.Ordinal);
        Assert.Matches(@"-p:JaneUiAccess=\$\(", publish);
    }

    [Fact]
    public void TheInstallerDoesNotCarryTheLanguageModelRuntime()
    {
        // 1.4 GB, and Jane fetches it itself from Settings and from first run. An installer that
        // bundled it would turn a one-minute download into a twenty-minute one for a feature
        // dictation does not need in order to work.
        Assert.DoesNotContain("-IncludeOllama", MakeSetup, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingNeedsNoAdministrator()
    {
        var script = SetupScript;

        // Without uiAccess there is nothing that needs Program Files, so there is nothing that
        // needs a UAC prompt -- and an unsigned installer asking for elevation is a worse first
        // impression than one that does not.
        Assert.Matches(@"(?m)^PrivilegesRequired=lowest\s*$", script);

        // Still possible for somebody who wants it machine-wide.
        Assert.Matches(@"(?m)^PrivilegesRequiredOverridesAllowed=.*commandline", script);
    }

    [Fact]
    public void TheInstallerRefusesAMachineJaneCannotRunOn()
    {
        var script = SetupScript;

        Assert.Matches(@"(?m)^ArchitecturesAllowed=x64compatible\s*$", script);
        Assert.Matches(@"(?m)^ArchitecturesInstallIn64BitMode=x64compatible\s*$", script);
        Assert.Matches(@"(?m)^MinVersion=10\.0", script);
    }

    [Fact]
    public void AutostartIsWrittenExactlyTheWayTheTrayWritesIt()
    {
        // Two writers, one value. If the installer and the tray's "Start with Windows" toggle
        // disagreed about the key, the name or the quoting, unticking the box in the tray would
        // leave the installer's entry behind and Jane would keep starting.
        var script = SetupScript;

        Assert.Contains($"Subkey: \"{AutostartRegistration.RunKeyPath}\"", script, StringComparison.Ordinal);
        Assert.Contains($"ValueName: \"{AutostartRegistration.DefaultValueName}\"", script, StringComparison.Ordinal);

        // Quoted, because a path with a space in it is split by the shell otherwise.
        var tray = new AutostartRegistration(executablePath: @"C:\Somewhere\Jane.exe");
        Assert.Equal("\"C:\\Somewhere\\Jane.exe\"", tray.Command);
        Assert.Contains("ValueData: \"\"\"{app}\\{#AppExe}\"\"\"", script, StringComparison.Ordinal);

        // And per-user, like the tray's.
        Assert.Matches(@"Root: HKCU; Subkey: ""Software\\Microsoft\\Windows\\CurrentVersion\\Run""", script);
        Assert.Contains("uninsdeletevalue", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningJaneIsClosedRatherThanLeftHoldingTheFileOpen()
    {
        // Jane lives in the tray and starts with Windows, so on every upgrade it is running.
        // Windows will not replace an executable that is open.
        var script = SetupScript;

        Assert.Contains("PrepareToInstall", script, StringComparison.Ordinal);
        Assert.Contains("taskkill", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InitializeUninstall", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallingKeepsTheUsersDataUnlessTheySayOtherwise()
    {
        var script = SetupScript;

        // A dictionary somebody spent a month building is not the installer's to delete. The
        // question is asked, and the answer nobody gave -- a silent uninstall -- is "keep it".
        Assert.Contains("{localappdata}\\Jane", script, StringComparison.Ordinal);
        Assert.Contains("SuppressibleMsgBox", script, StringComparison.Ordinal);
        Assert.Contains("MB_DEFBUTTON2", script, StringComparison.Ordinal);
        Assert.Contains("IDNO", script, StringComparison.Ordinal);
    }

    [Fact]
    public void JaneIsStartedAsTheUserNotAsTheInstaller()
    {
        // An all-users install runs elevated. A Jane launched from it would be elevated too, and
        // would then type into windows the user's own keyboard cannot reach -- until the next
        // sign-in, when it would quietly stop.
        Assert.Matches(@"(?m)^Filename: ""\{app\}\\\{#AppExe\}"".*runasoriginaluser", SetupScript);
    }

    [Fact]
    public void TheInstallerKnowsTheNameJaneUsesToStaySingle()
    {
        // Setup cannot install twice at once, and the mutex it uses for that must not be Jane's
        // own -- or starting the installer would look, to Jane, like a second Jane.
        var script = SetupScript;

        Assert.Matches(@"(?m)^SetupMutex=", script);
        Assert.DoesNotContain($"SetupMutex={SingleInstance.DefaultName}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLicenceAndTheAttributionsAreInstalledBesideTheProgram()
    {
        // Parakeet is CC-BY-4.0 and Silero VAD is MIT. Both require attribution in what is
        // shipped, and "shipped" now means the installer.
        var script = SetupScript;

        Assert.Contains("LICENSE", script, StringComparison.Ordinal);
        Assert.Contains("NOTICE.md", Read("build", "publish.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeOutputFolderMeansRelativeToWhereTheCommandWasRun()
    {
        // Found by running it: "-OutputDirectory artifacts\setup-smoke" compiled without error
        // and wrote the installer under build\installer instead, because Inno Setup resolves a
        // relative path against the script's own folder.
        var script = MakeSetup;

        Assert.Contains("IsPathRooted($OutputDirectory)", script, StringComparison.Ordinal);
        Assert.Contains("IsPathRooted($PublishDirectory)", script, StringComparison.Ordinal);
        Assert.Contains("GetFullPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolThatBuildsTheInstallerIsPinnedByHash()
    {
        // The installer is built by a program downloaded from the internet. Every other download
        // in this project is verified against a hash in the source; this one is no different.
        var script = Read("build", "get-innosetup.ps1");

        Assert.Matches(@"\[string\]\$Sha256\s*=\s*'[0-9a-f]{64}'", script);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("throw", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleaseIsCutByPushingAVersionTag()
    {
        var workflow = Workflow;

        Assert.Matches(@"tags:\s*\[\s*'v\*'\s*\]", workflow);
        Assert.Contains("make-setup.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release", workflow, StringComparison.Ordinal);
        Assert.Contains("JaneSetup.exe", workflow, StringComparison.Ordinal);

        // Least privilege: writing a release is all this job is for.
        Assert.Matches(@"(?m)^permissions:\s*\n\s+contents: write\s*$", workflow.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReleaseInstallsAndUninstallsWhatItBuiltBeforePublishingIt()
    {
        // A built installer that has never been run is a guess. The runner is a clean machine,
        // which is exactly what a new user has.
        var workflow = Workflow;

        var install = workflow.IndexOf("/VERYSILENT", StringComparison.Ordinal);
        var publish = workflow.IndexOf("gh release", StringComparison.Ordinal);

        Assert.True(install >= 0, "The workflow never runs the installer it built.");
        Assert.True(install < publish, "The installer is published before it has been run.");
        Assert.Contains("unins000.exe", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryActionIsPinnedToACommit()
    {
        // A tag can be moved to point at different code; a commit hash cannot. This job holds a
        // token that can publish a release under the project's name.
        var uses = UsesLine().Matches(Workflow);

        Assert.NotEmpty(uses);
        Assert.All(uses, match => Assert.Matches("^[0-9a-f]{40}$", match.Groups["ref"].Value));
    }

    [Fact]
    public void TheReadmeLinksToTheFileTheReleaseProduces()
    {
        var readme = Read("README.md");

        // "latest/download" follows the newest release, so the link in the README never has to
        // be edited -- provided the file is always called the same thing.
        Assert.Contains(
            "https://github.com/Divici/Jane1.0/releases/latest/download/JaneSetup.exe",
            readme,
            StringComparison.Ordinal);

        Assert.Matches(@"(?m)^OutputBaseFilename=JaneSetup\s*$", SetupScript);

        // And it says what Windows is about to say, before Windows says it.
        Assert.Contains("SmartScreen", readme, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"uses:\s*[\w./-]+@(?<ref>\S+)")]
    private static partial Regex UsesLine();
}
