using Jane.Core.Abstractions;
using Jane.Core.Formatting;
using Jane.Core.History;

namespace Jane.Core.Modes;

/// <summary>What the spoken utterance asked Edit Mode to do.</summary>
public enum EditIntent
{
    /// <summary>Rewrite the selection according to the instruction. The general case.</summary>
    Rewrite,

    /// <summary>Remove the selection. "Delete that."</summary>
    Delete,

    /// <summary>Step back one edit. "Undo that", "go back one step".</summary>
    Undo,

    /// <summary>Restore the text as it was before Jane touched it. "Go back to the original".</summary>
    UndoAll,

    /// <summary>
    /// No command word at all -- the user simply spoke a corrected version.
    /// </summary>
    /// <remarks>
    /// Aqua supports this and it is genuinely the most natural form: you select a wrong sentence
    /// and say the right one. Treated as a replacement rather than an instruction.
    /// </remarks>
    Replace,
}

/// <param name="SubmitAfterInjection">
/// "Send it" in Dictation Mode. The Enter is synthesised only after injection has been verified --
/// submitting a form that did not receive the text is worse than not submitting.
/// </param>
public sealed record EditCommand(
    EditIntent Intent,
    string Instruction,
    bool SubmitAfterInjection = false);

/// <summary>
/// Parses a spoken utterance into an Edit Mode command.
/// </summary>
/// <remarks>
/// Keyword-anchored, matching every surveyed implementation: Windows Voice Access uses a fixed
/// "&lt;action&gt; that" grammar, Talon a mode, VoiceInk and OpenWhispr a separate trigger. Nothing
/// surveyed classifies with a model, and this does not either -- the transcript is matched against
/// a closed set of command phrases and anything else is an instruction to the LLM.
/// </remarks>
public static class EditCommandParser
{
    private static readonly string[] UndoPhrases =
    [
        "undo that", "undo", "go back one step", "go back a step", "step back", "revert that",
    ];

    private static readonly string[] UndoAllPhrases =
    [
        "go back to the original", "back to the original", "restore the original",
        "undo everything", "undo all of that",
    ];

    private static readonly string[] DeletePhrases =
    [
        "delete that", "delete it", "remove that", "get rid of that", "scrap that",
    ];

    /// <summary>
    /// Phrases that mark the utterance as an instruction rather than a replacement.
    /// </summary>
    /// <remarks>
    /// Without this, "the meeting is on Wednesday" spoken over a selection would be read as an
    /// instruction and handed to the LLM as one, which is a coin flip. With it, an utterance that
    /// begins like an instruction is treated as one and everything else replaces the selection.
    /// </remarks>
    private static readonly string[] InstructionOpeners =
    [
        "make it", "make this", "make that", "fix", "correct", "rewrite", "reword", "rephrase",
        "shorten", "lengthen", "expand", "abbreviate", "summarise", "summarize", "translate",
        "change", "replace", "turn it into", "turn this into", "convert", "capitalise",
        "capitalize", "format", "bullet", "tidy", "clean up", "proofread", "simplify",
    ];

    private const string SendItPhrase = "send it";

    public static EditCommand Parse(string transcript)
    {
        var normalised = FormattingLexicon.Normalise(transcript);

        if (UndoAllPhrases.Any(p => normalised.Contains(p, StringComparison.Ordinal)))
        {
            return new EditCommand(EditIntent.UndoAll, transcript);
        }

        // Checked after UndoAll, because "go back to the original" contains "go back".
        if (UndoPhrases.Any(p => IsWholeUtterance(normalised, p)))
        {
            return new EditCommand(EditIntent.Undo, transcript);
        }

        if (DeletePhrases.Any(p => IsWholeUtterance(normalised, p)))
        {
            return new EditCommand(EditIntent.Delete, transcript);
        }

        var opensWithInstruction = InstructionOpeners.Any(o =>
            normalised.StartsWith(o + " ", StringComparison.Ordinal) ||
            string.Equals(normalised, o, StringComparison.Ordinal));

        return new EditCommand(
            opensWithInstruction ? EditIntent.Rewrite : EditIntent.Replace,
            transcript);
    }

    /// <summary>
    /// Strips a trailing "send it" from a dictation, returning whether it was there.
    /// </summary>
    /// <remarks>
    /// Only trailing, and only as the final words. "Send it to Kate tomorrow" is a sentence
    /// somebody dictated, not a request to press Enter, and stripping it there would delete two
    /// words of their text and submit a half-finished message.
    /// </remarks>
    public static (string Text, bool Submit) StripSendIt(string transcript)
    {
        var trimmed = transcript.TrimEnd();
        var normalised = FormattingLexicon.Normalise(trimmed);

        if (!normalised.EndsWith(SendItPhrase, StringComparison.Ordinal))
        {
            return (transcript, false);
        }

        // Walk back over the raw text by the number of words in the phrase, so the original
        // casing and punctuation of everything before it survive untouched.
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return (string.Empty, true);
        }

        var kept = string.Join(' ', words[..^2]).TrimEnd(' ', ',', ';');
        return (kept, true);
    }

    private static bool IsWholeUtterance(string normalised, string phrase) =>
        string.Equals(normalised, phrase, StringComparison.Ordinal) ||
        normalised.StartsWith(phrase + " ", StringComparison.Ordinal) ||
        normalised.EndsWith(" " + phrase, StringComparison.Ordinal);
}

/// <param name="Text">What should end up in place of the selection. Empty for a delete.</param>
/// <param name="ShouldInject">False when nothing is to be written -- an undo with an empty stack.</param>
public sealed record EditOutcome(
    bool ShouldInject,
    string Text,
    EditIntent Intent,
    string Message);

/// <summary>
/// Turns a parsed Edit Mode command plus a selection into the text that replaces it.
/// </summary>
/// <remarks>
/// The undo stack is pushed *before* the replacement, not after, so "undo that" restores what was
/// there rather than what Jane just wrote. The distinction matters on the second consecutive edit.
/// </remarks>
public sealed class EditModeHandler(UndoStack undo)
{
    public UndoStack Undo { get; } = undo;

    public async Task<EditOutcome> ApplyAsync(
        EditCommand command,
        SelectionResult selection,
        Func<string, string, CancellationToken, Task<string>> rewrite,
        CancellationToken cancellationToken)
    {
        switch (command.Intent)
        {
            case EditIntent.Undo:
                {
                    var entry = Undo.Pop();
                    return entry is null
                        ? new EditOutcome(false, string.Empty, command.Intent, "There is nothing to undo here.")
                        : new EditOutcome(true, entry.Text, command.Intent, "Undone.");
                }

            case EditIntent.UndoAll:
                {
                    var entry = Undo.PopToOriginal();
                    return entry is null
                        ? new EditOutcome(false, string.Empty, command.Intent, "There is nothing to restore here.")
                        : new EditOutcome(true, entry.Text, command.Intent, "Restored the original.");
                }

            case EditIntent.Delete:
                Undo.Push(selection.Text, string.Empty, "delete");
                return new EditOutcome(true, string.Empty, command.Intent, "Deleted.");

            case EditIntent.Replace:
                Undo.Push(selection.Text, command.Instruction, "replace");
                return new EditOutcome(true, command.Instruction, command.Intent, "Replaced.");

            case EditIntent.Rewrite:
            default:
                {
                    var rewritten = await rewrite(selection.Text, command.Instruction, cancellationToken);

                    if (string.IsNullOrWhiteSpace(rewritten))
                    {
                        // An empty rewrite would silently delete the user's paragraph. Refusing is the
                        // only safe reading of a model that returned nothing.
                        return new EditOutcome(false, string.Empty, command.Intent,
                            "The rewrite came back empty, so nothing was changed.");
                    }

                    Undo.Push(selection.Text, rewritten, command.Instruction);
                    return new EditOutcome(true, rewritten, command.Intent, "Rewritten.");
                }
        }
    }

}
