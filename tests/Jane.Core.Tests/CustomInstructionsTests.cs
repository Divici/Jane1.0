using Jane.Core.Instructions;
using Jane.Core.Platform;
using Jane.Core.Storage;

namespace Jane.Core.Tests;

/// <summary>
/// Custom Instructions: free-text style rules, per-app overrides, and the bypass they take away.
/// </summary>
/// <remarks>
/// The per-app table is a deliberate improvement on Aqua, which expresses per-app behaviour only
/// as natural language inside one global instruction block.
/// </remarks>
public sealed class CustomInstructionsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-instructions-test");
    private readonly JaneDatabase _database;
    private readonly CustomInstructions _instructions;

    public CustomInstructionsTests()
    {
        _database = JaneDatabase.Open(new JanePaths(_root.FullName, Path.Combine(_root.FullName, "models")));
        _instructions = new CustomInstructions(_database);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PerAppInstructionChangesOutputForThatAppOnly()
    {
        await _instructions.SetForAppAsync("code", "Prefer lowercase and no trailing period.", Token);

        var inEditor = _instructions.ResolveFor("code");
        var elsewhere = _instructions.ResolveFor("outlook");

        Assert.Contains("no trailing period", inEditor.Combined, StringComparison.Ordinal);
        Assert.True(elsewhere.IsEmpty);
        Assert.Equal(string.Empty, elsewhere.Combined);
    }

    [Fact]
    public async Task AnAppWithInstructionsSuppressesTheBypassForThatAppOnly()
    {
        await _instructions.SetForAppAsync("slack", "Keep it casual.", Token);

        Assert.True(_instructions.HasInstructionsFor("slack"));
        Assert.False(_instructions.HasInstructionsFor("notepad"));
    }

    [Fact]
    public async Task GlobalInstructionsSuppressTheBypassEverywhere()
    {
        // Global rules are active in every app by definition, so bypassing anywhere would skip
        // them silently -- which is exactly the failure BLOCKER #9 describes.
        await _instructions.SetGlobalAsync("Never use exclamation marks.", Token);

        Assert.True(_instructions.HasInstructionsFor("notepad"));
        Assert.True(_instructions.HasInstructionsFor("code"));
        Assert.True(_instructions.HasInstructionsFor(null));
    }

    [Fact]
    public async Task GlobalAndPerAppCompose_WithTheAppRulesLastSoTheyWin()
    {
        await _instructions.SetGlobalAsync("Write in British English.", Token);
        await _instructions.SetForAppAsync("code", "Use American spelling for identifiers.", Token);

        var resolved = _instructions.ResolveFor("code");

        Assert.Equal("Write in British English.", resolved.Global);
        Assert.Equal("Use American spelling for identifiers.", resolved.PerApp);

        var globalAt = resolved.Combined.IndexOf("British", StringComparison.Ordinal);
        var appAt = resolved.Combined.IndexOf("American", StringComparison.Ordinal);
        Assert.True(globalAt >= 0 && appAt > globalAt);
    }

    [Fact]
    public async Task ProcessNameMatchingIsCaseInsensitiveAndIgnoresTheExtension()
    {
        // TargetWindow.ProcessName is already lower-case with no extension, but the settings
        // window lets a person type the name by hand.
        await _instructions.SetForAppAsync("Code.exe", "Terse.", Token);

        Assert.True(_instructions.HasInstructionsFor("code"));
        Assert.Contains("Terse.", _instructions.ResolveFor("CODE").Combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingEmptyTextRemovesTheOverride()
    {
        await _instructions.SetForAppAsync("slack", "Keep it casual.", Token);
        await _instructions.SetForAppAsync("slack", "   ", Token);

        Assert.False(_instructions.HasInstructionsFor("slack"));
        Assert.Empty(_instructions.All);
    }

    [Fact]
    public async Task DisabledOverrideIsNotResolvedAndDoesNotSuppressTheBypass()
    {
        var entry = await _instructions.SetForAppAsync("slack", "Keep it casual.", Token);
        Assert.NotNull(entry);
        await _instructions.SetEnabledAsync(entry.Id, false, Token);

        Assert.False(_instructions.HasInstructionsFor("slack"));
        Assert.True(_instructions.ResolveFor("slack").IsEmpty);
        Assert.Single(_instructions.All);
    }

    [Fact]
    public async Task InstructionsPersistAcrossRestart()
    {
        await _instructions.SetGlobalAsync("Never use exclamation marks.", Token);
        await _instructions.SetForAppAsync("code", "Terse.", Token);
        _database.Dispose();

        using var reopened = JaneDatabase.Open(new JanePaths(_root.FullName));
        var reloaded = new CustomInstructions(reopened);

        Assert.Equal("Never use exclamation marks.", reloaded.Global);
        Assert.Equal("Terse.", reloaded.ResolveFor("code").PerApp);
    }

    [Fact]
    public async Task GlobalInstructionsAreASingleRowThatSettingTwiceReplaces()
    {
        await _instructions.SetGlobalAsync("First.", Token);
        await _instructions.SetGlobalAsync("Second.", Token);

        Assert.Equal("Second.", _instructions.Global);
        Assert.Single(_instructions.All);
    }

    [Fact]
    public async Task ClearingGlobalRestoresTheBypassWhereNoAppRuleApplies()
    {
        await _instructions.SetGlobalAsync("Never use exclamation marks.", Token);
        await _instructions.SetForAppAsync("slack", "Keep it casual.", Token);

        await _instructions.SetGlobalAsync(null, Token);

        Assert.Null(_instructions.Global);
        Assert.False(_instructions.HasInstructionsFor("notepad"));
        Assert.True(_instructions.HasInstructionsFor("slack"));
    }

    [Fact]
    public void EmptyInstructionsSuppressNothing()
    {
        Assert.False(_instructions.HasInstructionsFor("notepad"));
        Assert.True(_instructions.ResolveFor("notepad").IsEmpty);
        Assert.Null(_instructions.Global);
    }

    public void Dispose()
    {
        _database.Dispose();
        _root.Delete(recursive: true);
    }
}
