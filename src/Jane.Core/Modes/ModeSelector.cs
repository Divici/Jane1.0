using Jane.Core.Abstractions;

namespace Jane.Core.Modes;

/// <summary>What a held hotkey means right now.</summary>
public enum DictationModeKind
{
    /// <summary>Speech becomes text. The default, and the fallback whenever anything is uncertain.</summary>
    Dictation,

    /// <summary>Speech is an instruction that rewrites the current selection in place.</summary>
    Edit,

    /// <summary>
    /// There is a selection, but Jane could not read it, so it will not guess.
    /// </summary>
    /// <remarks>
    /// This state exists because the alternative is catastrophic: with no readable selection, an
    /// Edit-Mode utterance like "make it shorter" would be *typed into the document* as those four
    /// words. Saying "unavailable here" is worse UX and enormously better behaviour.
    /// </remarks>
    EditUnavailable,
}

/// <param name="Text">The selected text, when it could be read.</param>
/// <param name="Source">How it was obtained -- reported so the layered probe is debuggable.</param>
public sealed record SelectionResult(bool HasSelection, string Text, SelectionSource Source)
{
    public static SelectionResult None { get; } = new(false, string.Empty, SelectionSource.None);

    /// <summary>A selection exists but its content could not be retrieved.</summary>
    public static SelectionResult Unreadable { get; } = new(true, string.Empty, SelectionSource.Unreadable);

    public int Length => Text.Length;
}

public enum SelectionSource
{
    None,

    /// <summary>UIA reported the selection directly. The cheap path.</summary>
    Uia,

    /// <summary>
    /// A synthetic Ctrl+C against a saved-and-restored clipboard.
    /// </summary>
    /// <remarks>
    /// The fallback for Chromium and Electron, whose UIA text providers are incomplete -- which is
    /// exactly where Edit Mode matters most, since that is Docs, Slack, VS Code and every web
    /// editor.
    /// </remarks>
    ClipboardProbe,

    /// <summary>Something is selected and neither probe could read it.</summary>
    Unreadable,
}

/// <param name="MaxSelectionLength">
/// Above this, Edit Mode falls back to Dictation. Aqua caps at 6,000 characters and Jane matches:
/// past that the prompt stops fitting a sensible context window and a rewrite starts silently
/// truncating the user's document.
/// </param>
public sealed record ModeSelectorOptions
{
    public int MaxSelectionLength { get; init; } = 6000;
}

/// <param name="Reason">Written for the overlay when the mode is not what the user expected.</param>
public sealed record ModeDecision(
    DictationModeKind Mode,
    SelectionResult Selection,
    string Reason)
{
    public static ModeDecision Dictate(string reason) =>
        new(DictationModeKind.Dictation, SelectionResult.None, reason);
}

/// <summary>
/// Decides between Dictation and Edit Mode.
/// </summary>
/// <remarks>
/// Deterministic, never a classifier. Not one surveyed implementation classifies command-versus-
/// content with a model: Aqua and VoiceInk key off a live selection, Talon uses an explicit mode,
/// OpenWhispr uses a second hotkey. A live selection is the trigger here too.
/// <para>
/// Every uncertain case resolves to Dictation. The asymmetry is stark: dictating when the user
/// wanted an edit types a sentence they can undo, while editing when they wanted dictation
/// rewrites text they did not ask you to touch.
/// </para>
/// </remarks>
public sealed class ModeSelector(ModeSelectorOptions? options = null)
{
    private readonly ModeSelectorOptions _options = options ?? new ModeSelectorOptions();

    /// <summary>
    /// Control types that always mean Dictation, whatever is selected.
    /// </summary>
    /// <remarks>
    /// A browser address bar reports a selection like any other edit control, and "make it
    /// shorter" spoken at one is unambiguously meant as text to search for. Aqua disables Edit
    /// Mode in address and search bars for the same reason.
    /// </remarks>
    public static IReadOnlyList<string> DictationOnlyClassNames { get; } =
    [
        "Chrome_OmniboxView",
        "Address Band Root",
        "SearchBox",
        "Edit",
    ];

    public ModeDecision Decide(TargetWindow target, SelectionResult selection, string? controlType = null)
    {
        if (!selection.HasSelection)
        {
            return ModeDecision.Dictate("Nothing is selected, so this is ordinary dictation.");
        }

        if (IsDictationOnlySurface(target, controlType))
        {
            return ModeDecision.Dictate(
                "This looks like an address or search field, where a spoken phrase is what you want typed, not an instruction.");
        }

        if (selection.Source == SelectionSource.Unreadable)
        {
            return new ModeDecision(
                DictationModeKind.EditUnavailable,
                selection,
                "Something is selected here, but Jane cannot read it, so it will not risk typing your instruction into the document.");
        }

        if (selection.Length > _options.MaxSelectionLength)
        {
            return ModeDecision.Dictate(
                $"The selection is {selection.Length:N0} characters, over the {_options.MaxSelectionLength:N0} limit for an in-place rewrite.");
        }

        if (string.IsNullOrWhiteSpace(selection.Text))
        {
            // A degenerate range -- TextPattern returns start == end at a bare caret -- is not a
            // selection at all, whatever the provider reported.
            return ModeDecision.Dictate("The selection is empty, so this is ordinary dictation.");
        }

        return new ModeDecision(DictationModeKind.Edit, selection, "Editing the selected text.");
    }

    private static bool IsDictationOnlySurface(TargetWindow target, string? controlType)
    {
        if (controlType is not null &&
            DictationOnlyClassNames.Any(c => controlType.Contains(c, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Chromium reports its omnibox through a distinctive class on the window itself in some
        // shells; checking both is cheap and the failure it prevents is typing an instruction
        // into a search box.
        return DictationOnlyClassNames.Any(c =>
            target.WindowClass.Contains(c, StringComparison.OrdinalIgnoreCase));
    }
}
