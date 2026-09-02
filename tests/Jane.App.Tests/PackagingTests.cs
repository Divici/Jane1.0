using System.Xml.Linq;

namespace Jane.App.Tests;

/// <summary>
/// Phase 13 owns packaging, but the manifest exists from Phase 0 and its contents are a
/// security decision, not a build detail -- so they are asserted from the start.
/// </summary>
public sealed class PackagingTests
{
    private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";

    [Fact]
    public void Manifest_RequestsUiAccessAsInvoker()
    {
        // uiAccess is what lets Jane type into an elevated window without Jane itself being
        // elevated. asInvoker matters just as much: a dictation tool that demanded admin would
        // be a far larger attack surface than the feature is worth.
        var level = ExecutionLevelElement();

        Assert.Equal("true", level.Attribute("uiAccess")?.Value);
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
    }

    [Fact]
    public void Manifest_DeclaresPerMonitorV2DpiAwareness()
    {
        // The overlay is positioned against a caret rect in physical pixels. Anything less than
        // PerMonitorV2 puts it in the wrong place the moment a second monitor has a different
        // scale factor.
        var manifest = LoadManifest();
        var dpi = manifest.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "dpiAwareness");

        Assert.NotNull(dpi);
        Assert.Equal("PerMonitorV2", dpi.Value);
    }

    [Fact]
    public void Manifest_DeclaresWindows10Or11Compatibility()
    {
        var manifest = LoadManifest();
        var supported = manifest.Descendants()
            .Where(e => e.Name.LocalName == "supportedOS")
            .Select(e => e.Attribute("Id")?.Value)
            .ToArray();

        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", supported);
    }

    private static XElement ExecutionLevelElement()
    {
        var manifest = LoadManifest();
        var level = manifest
            .Element(AsmV1 + "trustInfo")?
            .Descendants(AsmV3 + "requestedExecutionLevel")
            .FirstOrDefault()
            ?? manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "requestedExecutionLevel");

        Assert.NotNull(level);
        return level;
    }

    private static XElement LoadManifest()
    {
        var path = FindRepoFile(Path.Combine("src", "Jane.App", "app.manifest"));
        return XDocument.Load(path).Root!;
    }

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
