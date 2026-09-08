using Jane.Core.Abstractions;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// The per-app strategy table.
/// </summary>
/// <remarks>
/// There is no strategy that works everywhere. Ctrl+V is not paste in a console, rapid
/// synthetic WM_CHAR streams are dropped by Electron's renderer, and Office answers a paste
/// with format negotiation and a Paste Options button. The table encodes which failure mode
/// each app has, so the injector never has to guess.
/// </remarks>
[Trait("Category", "Injection")]
public sealed class InjectionStrategySelectorTests
{
    private static TargetWindow Window(string processName, string windowClass = "SomeClass") =>
        new(Handle: 0x1234, ProcessId: 42, ProcessName: processName, WindowClass: windowClass, WindowTitle: "t");

    [Theory]
    [InlineData("windowsterminal")]
    [InlineData("openconsole")]
    [InlineData("conhost")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void Terminals_AlwaysGetUnicode_NoMatterHowLongTheText(string processName)
    {
        var selector = new InjectionStrategySelector();

        var decision = selector.Select(Window(processName), characterCount: 10_000);

        // Ctrl+V is not paste in a console, and Windows Terminal interrupts a multiline paste
        // with a confirmation dialog. Clipboard here does not merely fail -- it stalls.
        Assert.Equal(InjectionStrategy.Unicode, decision.Strategy);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    public void ConsoleWindowClasses_GetUnicode_EvenWhenTheProcessIsUnknown(string windowClass)
    {
        // A console hosted by an arbitrary exe -- python.exe, node.exe -- is still a console.
        var selector = new InjectionStrategySelector();

        var decision = selector.Select(Window("somethingunknown", windowClass), characterCount: 5_000);

        Assert.Equal(InjectionStrategy.Unicode, decision.Strategy);
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("discord")]
    [InlineData("code")]
    [InlineData("teams")]
    public void ElectronApps_GetClipboardForAnythingButAShortPhrase(string processName)
    {
        var selector = new InjectionStrategySelector();

        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window(processName), 20).Strategy);
        Assert.Equal(InjectionStrategy.Clipboard, selector.Select(Window(processName), 200).Strategy);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("brave")]
    [InlineData("firefox")]
    public void Browsers_GetUnicodeForShortTextAndClipboardForLong(string processName)
    {
        var selector = new InjectionStrategySelector();

        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window(processName), 50).Strategy);
        Assert.Equal(InjectionStrategy.Clipboard, selector.Select(Window(processName), 900).Strategy);
    }

    [Theory]
    [InlineData("winword")]
    [InlineData("excel")]
    [InlineData("outlook")]
    [InlineData("powerpnt")]
    public void Office_PrefersTypingWellPastTheDefaultThreshold(string processName)
    {
        // Office answers a paste with AutoCorrect-on-paste and a Paste Options smart tag.
        // Synthetic typing looks like typing, so the Unicode ceiling is deliberately high.
        var selector = new InjectionStrategySelector();
        var @default = InjectionStrategySelectorOptions.Default.Fallback.UnicodeMaxCharacters;

        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window(processName), @default + 1).Strategy);
        Assert.Equal(InjectionStrategy.Clipboard, selector.Select(Window(processName), 20_000).Strategy);
    }

    [Fact]
    public void UnknownProcess_FallsBackToUnicodeUnderTheThresholdAndClipboardAbove()
    {
        var selector = new InjectionStrategySelector();
        var threshold = InjectionStrategySelectorOptions.Default.Fallback.UnicodeMaxCharacters;

        // Deliberately an application nothing has a rule for. Notepad used to stand in here and no
        // longer can: it has a rule of its own now, which is the point of that rule.
        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window("someeditor"), threshold).Strategy);
        Assert.Equal(InjectionStrategy.Clipboard, selector.Select(Window("someeditor"), threshold + 1).Strategy);
    }

    [Fact]
    public void ProcessNamesMatchWithoutRegardToCaseOrExtension()
    {
        var selector = new InjectionStrategySelector();

        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window("WindowsTerminal"), 10_000).Strategy);
        Assert.Equal(InjectionStrategy.Unicode, selector.Select(Window("WindowsTerminal.exe"), 10_000).Strategy);
    }

    [Fact]
    public void TableCarriesAnEntryForEveryAppFamilyThePlanNames()
    {
        var options = InjectionStrategySelectorOptions.Default;

        Assert.Contains("chrome", options.ProcessRules.Keys);
        Assert.Contains("slack", options.ProcessRules.Keys);
        Assert.Contains("windowsterminal", options.ProcessRules.Keys);
        Assert.Contains("conhost", options.ProcessRules.Keys);
        Assert.Contains("winword", options.ProcessRules.Keys);
        Assert.All(options.ProcessRules.Values, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Reason)));
        Assert.False(string.IsNullOrWhiteSpace(options.Fallback.Reason));
    }

    [Fact]
    public void AUserOverrideBeatsTheBuiltInEntryForThatProcess()
    {
        // Settings exposes a per-app strategy override; a user who finds one app misbehaving
        // must be able to correct it without a code change.
        var rules = new Dictionary<string, InjectionRule>(
            InjectionStrategySelectorOptions.Default.ProcessRules, StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(UnicodeMaxCharacters: int.MaxValue, InjectionStrategy.Unicode, "user override"),
        };
        var selector = new InjectionStrategySelector(
            InjectionStrategySelectorOptions.Default with { ProcessRules = rules });

        var decision = selector.Select(Window("chrome"), characterCount: 5_000);

        Assert.Equal(InjectionStrategy.Unicode, decision.Strategy);
        Assert.Contains("user override", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTargetStillProducesADecisionRatherThanThrowing()
    {
        var selector = new InjectionStrategySelector();

        var decision = selector.Select(TargetWindow.None, characterCount: 10);

        Assert.Equal(InjectionStrategy.Unicode, decision.Strategy);
    }
}
