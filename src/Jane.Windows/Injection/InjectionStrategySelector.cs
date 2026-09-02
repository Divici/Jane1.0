using Jane.Core.Abstractions;

namespace Jane.Windows.Injection;

/// <param name="UnicodeMaxCharacters">
/// The longest text this app should receive as synthetic keystrokes. Above it, the injector
/// switches to <paramref name="LongTextStrategy"/>.
/// </param>
/// <param name="LongTextStrategy">What to use once the text is longer than the ceiling.</param>
/// <param name="Reason">Recorded on the decision and written to history, so a wrong choice is diagnosable.</param>
public sealed record InjectionRule(
    int UnicodeMaxCharacters,
    InjectionStrategy LongTextStrategy,
    string Reason);

/// <param name="ProcessRules">Keyed on <see cref="TargetWindow.ProcessName"/>, case-insensitive.</param>
/// <param name="WindowClassRules">
/// Keyed on window class. Consulted first, because a console window is a console whatever exe
/// happens to be hosting it -- python.exe and node.exe both put up a ConsoleWindowClass.
/// </param>
/// <param name="Fallback">Everything else. Deliberately conservative.</param>
public sealed record InjectionStrategySelectorOptions(
    IReadOnlyDictionary<string, InjectionRule> ProcessRules,
    IReadOnlyDictionary<string, InjectionRule> WindowClassRules,
    InjectionRule Fallback)
{
    private const string TerminalReason =
        "Terminal: Ctrl+V is not paste in a console, and Windows Terminal interrupts a multiline paste with a confirmation dialog. Synthetic Unicode keystrokes are the only strategy that always lands.";

    private const string ElectronReason =
        "Electron: the renderer silently drops long synthetic WM_CHAR streams (OpenWhispr#829), so anything past a short phrase goes via the clipboard.";

    private const string ChromiumReason =
        "Browser: short text types cleanly and leaves the clipboard alone; long text is dropped by the renderer's input queue, so it pastes instead.";

    private const string OfficeReason =
        "Office: a paste triggers AutoCorrect-on-paste and the Paste Options smart tag, so Jane types instead until the text gets genuinely long.";

    private const string FallbackReason =
        "Default: type short text so the clipboard is never touched; paste long text so no single app has to absorb thousands of synthetic keystrokes.";

    /// <summary>
    /// Text at or below this length is typed. 200 is the point at which synthetic keystroke
    /// streams start being dropped in the wild (OpenWhispr#829), and it comfortably covers a
    /// normal spoken sentence, which is the overwhelmingly common case.
    /// </summary>
    public const int DefaultUnicodeMaxCharacters = 200;

    public static InjectionStrategySelectorOptions Default { get; } = Build();

    private static InjectionStrategySelectorOptions Build()
    {
        var terminal = new InjectionRule(int.MaxValue, InjectionStrategy.Unicode, TerminalReason);
        var electron = new InjectionRule(64, InjectionStrategy.Clipboard, ElectronReason);
        var chromium = new InjectionRule(DefaultUnicodeMaxCharacters, InjectionStrategy.Clipboard, ChromiumReason);
        var office = new InjectionRule(4_000, InjectionStrategy.Clipboard, OfficeReason);

        var processRules = new Dictionary<string, InjectionRule>(StringComparer.OrdinalIgnoreCase);
        Add(processRules, terminal,
            "windowsterminal", "wt", "openconsole", "conhost", "cmd", "powershell", "pwsh",
            "mintty", "alacritty", "wezterm", "wezterm-gui", "putty", "kitty");
        Add(processRules, electron,
            "code", "code - insiders", "cursor", "windsurf", "slack", "discord", "teams",
            "ms-teams", "notion", "obsidian", "signal", "whatsapp", "spotify", "figma",
            "postman", "element", "todoist");
        Add(processRules, chromium,
            "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx", "chromium", "firefox");
        Add(processRules, office,
            "winword", "excel", "powerpnt", "outlook", "onenote", "msaccess", "mspub");

        var windowClassRules = new Dictionary<string, InjectionRule>(StringComparer.OrdinalIgnoreCase);
        Add(windowClassRules, terminal,
            "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty");

        return new InjectionStrategySelectorOptions(
            processRules,
            windowClassRules,
            new InjectionRule(DefaultUnicodeMaxCharacters, InjectionStrategy.Clipboard, FallbackReason));

        static void Add(Dictionary<string, InjectionRule> map, InjectionRule rule, params string[] keys)
        {
            foreach (var key in keys)
            {
                map[key] = rule;
            }
        }
    }
}

/// <param name="Reason">Human-readable justification, carried into history alongside the result.</param>
public sealed record InjectionDecision(InjectionStrategy Strategy, string Reason);

/// <summary>
/// Chooses between typing the text and pasting it, per target app.
/// </summary>
/// <remarks>
/// Neither strategy is universally correct, and both fail quietly rather than loudly when they
/// are wrong -- a terminal swallows Ctrl+V, Electron drops keystrokes past a couple of hundred
/// characters. The table names the failure mode each app has so the choice is a decision with a
/// recorded reason rather than a guess.
/// </remarks>
public sealed class InjectionStrategySelector(InjectionStrategySelectorOptions? options = null)
{
    private readonly InjectionStrategySelectorOptions _options = options ?? InjectionStrategySelectorOptions.Default;

    /// <summary>The table in force, so settings can show and override it per app.</summary>
    public InjectionStrategySelectorOptions Options => _options;

    public InjectionDecision Select(TargetWindow target, int characterCount)
    {
        var rule = Match(target);
        var strategy = characterCount <= rule.UnicodeMaxCharacters
            ? InjectionStrategy.Unicode
            : rule.LongTextStrategy;

        return new InjectionDecision(strategy, rule.Reason);
    }

    private InjectionRule Match(TargetWindow target)
    {
        if (!string.IsNullOrEmpty(target.WindowClass) &&
            _options.WindowClassRules.TryGetValue(target.WindowClass, out var byClass))
        {
            return byClass;
        }

        // FocusedAppIdentity already strips ".exe", but a caller constructing a TargetWindow by
        // hand -- history re-inject, a test, settings -- may not have, so the lookup tolerates it.
        var processName = target.ProcessName;
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processName = processName[..^4];
        }

        return _options.ProcessRules.TryGetValue(processName, out var byProcess)
            ? byProcess
            : _options.Fallback;
    }
}
