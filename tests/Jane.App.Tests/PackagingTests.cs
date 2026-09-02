using System.Xml.Linq;

namespace Jane.App.Tests;

/// <summary>
/// Phase 13 owns packaging, but the manifests exist from Phase 0 and their contents are security
/// decisions, not build details -- so they are asserted from the start.
/// </summary>
public sealed class PackagingTests
{
    private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";

    [Fact]
    public void ShippedManifest_RequestsUiAccessAsInvoker()
    {
        // uiAccess is what lets Jane type into an elevated window without Jane itself being
        // elevated. asInvoker matters just as much: a dictation tool that demanded admin would be
        // a far larger attack surface than the feature is worth.
        var level = ExecutionLevelElement("app.uiaccess.manifest");

        Assert.Equal("true", level.Attribute("uiAccess")?.Value);
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
    }

    [Fact]
    public void DevelopmentManifest_DoesNotRequestUiAccess_SoTheAppCanActuallyBeRun()
    {
        // Windows refuses to *start* an unsigned uiAccess binary from outside a secure location,
        // failing with "A referral was returned from the server." A single manifest declaring
        // uiAccess="true" would therefore make `dotnet run --project src/Jane.App` impossible
        // until Phase 13 signs and installs the binary -- which is also every earlier phase's
        // acceptance step. The two manifests are selected by -p:JaneUiAccess.
        var level = ExecutionLevelElement("app.manifest");

        Assert.Equal("false", level.Attribute("uiAccess")?.Value);
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
    }

    [Fact]
    public void BothManifests_AreIdenticalApartFromTheUiAccessAttributeAndItsComment()
    {
        // The shipped manifest must not drift from the one that is actually run during
        // development, or Phase 13 would be the first time the real manifest is exercised.
        var dev = LoadManifest("app.manifest");
        var shipped = LoadManifest("app.uiaccess.manifest");

        Assert.Equal(DescribeIgnoringUiAccess(dev), DescribeIgnoringUiAccess(shipped));
    }

    [Theory]
    [InlineData("app.manifest")]
    [InlineData("app.uiaccess.manifest")]
    public void Manifests_DeclarePerMonitorV2DpiAwareness(string manifestFile)
    {
        // The overlay is positioned against a caret rect in physical pixels. Anything less than
        // PerMonitorV2 puts it in the wrong place the moment a second monitor has a different
        // scale factor.
        var dpi = LoadManifest(manifestFile).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "dpiAwareness");

        Assert.NotNull(dpi);
        Assert.Equal("PerMonitorV2", dpi.Value);
    }

    [Theory]
    [InlineData("app.manifest")]
    [InlineData("app.uiaccess.manifest")]
    public void Manifests_DeclareWindows10Or11Compatibility(string manifestFile)
    {
        var supported = LoadManifest(manifestFile).Descendants()
            .Where(e => e.Name.LocalName == "supportedOS")
            .Select(e => e.Attribute("Id")?.Value)
            .ToArray();

        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", supported);
    }

    [Fact]
    public void ProjectSelectsTheShippedManifestOnlyWhenAskedTo()
    {
        // A default of true would silently reintroduce the un-launchable build.
        var csproj = File.ReadAllText(FindRepoFile(Path.Combine("src", "Jane.App", "Jane.App.csproj")));

        Assert.Contains("<JaneUiAccess Condition=\"'$(JaneUiAccess)' == ''\">false</JaneUiAccess>", csproj, StringComparison.Ordinal);
        Assert.Contains("app.uiaccess.manifest", csproj, StringComparison.Ordinal);
    }

    private static string DescribeIgnoringUiAccess(XElement manifest)
    {
        var clone = new XElement(manifest);
        foreach (var level in clone.Descendants().Where(e => e.Name.LocalName == "requestedExecutionLevel"))
        {
            level.Attribute("uiAccess")?.Remove();
        }

        // Comments legitimately differ -- each manifest explains why it is the one it is.
        clone.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());
        return clone.ToString();
    }

    private static XElement ExecutionLevelElement(string manifestFile)
    {
        var manifest = LoadManifest(manifestFile);
        var level = manifest
            .Element(AsmV1 + "trustInfo")?
            .Descendants(AsmV3 + "requestedExecutionLevel")
            .FirstOrDefault()
            ?? manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "requestedExecutionLevel");

        Assert.NotNull(level);
        return level;
    }

    private static XElement LoadManifest(string manifestFile) =>
        XDocument.Load(FindRepoFile(Path.Combine("src", "Jane.App", manifestFile))).Root!;

    internal static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} walking up from {AppContext.BaseDirectory}.");
    }
}
