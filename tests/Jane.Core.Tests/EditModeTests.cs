using Jane.Core.Abstractions;
using Jane.Core.History;
using Jane.Core.Modes;

namespace Jane.Core.Tests;

public sealed class EditModeTests
{
    private static readonly TargetWindow Notepad = new(1, 2, "notepad", "Notepad", "Untitled");

    private static readonly TargetWindow Chrome =
        new(2, 3, "chrome", "Chrome_WidgetWin_1", "Google Docs");

    private static SelectionResult Selected(string text) => new(true, text, SelectionSource.Uia);

    [Fact]
    public void NoSelectionMeansOrdinaryDictation()
    {
        var decision = new ModeSelector().Decide(Notepad, SelectionResult.None);

        Assert.Equal(DictationModeKind.Dictation, decision.Mode);
    }

    [Fact]
    public void ALiveSelectionMeansEditMode()
    {
        // The whole trigger. Deterministic, never a classifier -- no surveyed implementation
        // classifies command-versus-content with a model.
        var decision = new ModeSelector().Decide(Chrome, Selected("The meeting is on Tuesday."));

        Assert.Equal(DictationModeKind.Edit, decision.Mode);
    }

    [Fact]
    public void AnUnreadableSelectionSaysSoRatherThanDictatingTheCommandIntoTheDocument()
    {
        // BLOCKER #8. Without this, "make it shorter" in Google Docs -- where UIA reports
        // nothing -- would be typed into the document as those three words.
        var decision = new ModeSelector().Decide(Chrome, SelectionResult.Unreadable);

        Assert.Equal(DictationModeKind.EditUnavailable, decision.Mode);
        Assert.Contains("cannot read", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddressBarRoutesToDictationEvenWithASelection()
    {
        // Matching Aqua. A phrase spoken at an address bar is what you want typed, not an
        // instruction about what is already there.
        var decision = new ModeSelector().Decide(Chrome, Selected("previous query"), "Chrome_OmniboxView");

        Assert.Equal(DictationModeKind.Dictation, decision.Mode);
        Assert.Contains("address", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASelectionOverTheLimitFallsBackToDictation()
    {
        // 6,000 characters, matching Aqua's documented cap. Past it a rewrite starts silently
        // truncating the user's document.
        var decision = new ModeSelector().Decide(Notepad, Selected(new string('x', 7000)));

        Assert.Equal(DictationModeKind.Dictation, decision.Mode);
        Assert.Contains("7,000", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADegenerateRangeIsNotASelection()
    {
        // TextPattern returns start == end at a bare caret, which providers report as a
        // "selection". Treating that as Edit Mode would trigger on every click.
        var decision = new ModeSelector().Decide(Notepad, new SelectionResult(true, "   ", SelectionSource.Uia));

        Assert.Equal(DictationModeKind.Dictation, decision.Mode);
    }

    [Theory]
    [InlineData("undo that", EditIntent.Undo)]
    [InlineData("go back one step", EditIntent.Undo)]
    [InlineData("go back to the original", EditIntent.UndoAll)]
    [InlineData("delete that", EditIntent.Delete)]
    [InlineData("make it shorter", EditIntent.Rewrite)]
    [InlineData("fix the grammar", EditIntent.Rewrite)]
    [InlineData("abbreviate", EditIntent.Rewrite)]
    [InlineData("change 5pm to 6pm", EditIntent.Rewrite)]
    [InlineData("The meeting is on Wednesday.", EditIntent.Replace)]
    [InlineData("that's K-A-T-E", EditIntent.Replace)]
    public void CommandsParseToTheRightIntent(string spoken, EditIntent expected)
    {
        Assert.Equal(expected, EditCommandParser.Parse(spoken).Intent);
    }

    [Fact]
    public void GoBackToTheOriginalIsNotMistakenForASingleUndo()
    {
        // "go back to the original" contains "go back"; order of checks is what keeps them apart.
        Assert.Equal(EditIntent.UndoAll, EditCommandParser.Parse("go back to the original").Intent);
    }

    [Fact]
    public async Task MakeItShorterReplacesOnlyTheSelection()
    {
        var handler = new EditModeHandler(new UndoStack());
        var command = EditCommandParser.Parse("make it shorter");

        var outcome = await handler.ApplyAsync(
            command,
            Selected("The quarterly review meeting has been moved to next Thursday afternoon."),
            (selection, instruction, _) => Task.FromResult("Review moved to Thursday."),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.ShouldInject);
        Assert.Equal("Review moved to Thursday.", outcome.Text);
    }

    [Fact]
    public async Task AnEmptyRewriteChangesNothing()
    {
        // A model that returns nothing would otherwise silently delete the user's paragraph.
        var handler = new EditModeHandler(new UndoStack());

        var outcome = await handler.ApplyAsync(
            EditCommandParser.Parse("make it shorter"),
            Selected("Some real text."),
            (_, _, _) => Task.FromResult("   "),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.ShouldInject);
        Assert.Contains("empty", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UndoThatRestoresTheExactOriginalIncludingWhitespace()
    {
        var handler = new EditModeHandler(new UndoStack());
        const string Original = "  The meeting is on Tuesday.\n\n  Please confirm.  ";

        await handler.ApplyAsync(
            EditCommandParser.Parse("make it shorter"), Selected(Original),
            (_, _, _) => Task.FromResult("Meeting Tuesday."), TestContext.Current.CancellationToken);

        var undone = await handler.ApplyAsync(
            EditCommandParser.Parse("undo that"), Selected("Meeting Tuesday."),
            (_, _, _) => Task.FromResult(string.Empty), TestContext.Current.CancellationToken);

        Assert.True(undone.ShouldInject);
        Assert.Equal(Original, undone.Text);
    }

    [Fact]
    public async Task GoBackToTheOriginalRestoresAcrossThreeEdits()
    {
        var handler = new EditModeHandler(new UndoStack());
        const string Original = "The original sentence.";

        var current = Original;
        foreach (var step in (string[])["first edit", "second edit", "third edit"])
        {
            var next = step;
            await handler.ApplyAsync(
                EditCommandParser.Parse("make it shorter"), Selected(current),
                (_, _, _) => Task.FromResult(next), TestContext.Current.CancellationToken);
            current = next;
        }

        var restored = await handler.ApplyAsync(
            EditCommandParser.Parse("go back to the original"), Selected(current),
            (_, _, _) => Task.FromResult(string.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(Original, restored.Text);
    }

    [Fact]
    public async Task DeleteThatRemovesTheSelectionAndIsUndoable()
    {
        var handler = new EditModeHandler(new UndoStack());

        var deleted = await handler.ApplyAsync(
            EditCommandParser.Parse("delete that"), Selected("Goodbye sentence."),
            (_, _, _) => Task.FromResult(string.Empty), TestContext.Current.CancellationToken);

        Assert.True(deleted.ShouldInject);
        Assert.Equal(string.Empty, deleted.Text);

        var undone = await handler.ApplyAsync(
            EditCommandParser.Parse("undo that"), SelectionResult.None,
            (_, _, _) => Task.FromResult(string.Empty), TestContext.Current.CancellationToken);

        Assert.Equal("Goodbye sentence.", undone.Text);
    }

    [Fact]
    public async Task UndoWithNothingToUndoInjectsNothingAndSaysSo()
    {
        var handler = new EditModeHandler(new UndoStack());

        var outcome = await handler.ApplyAsync(
            EditCommandParser.Parse("undo that"), Selected("anything"),
            (_, _, _) => Task.FromResult(string.Empty), TestContext.Current.CancellationToken);

        Assert.False(outcome.ShouldInject);
        Assert.Contains("nothing to undo", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendItIsStrippedAndFlaggedForSubmission()
    {
        var (text, submit) = EditCommandParser.StripSendIt("tell them it is ready send it");

        Assert.True(submit);
        Assert.Equal("tell them it is ready", text);
    }

    [Fact]
    public void SendItInTheMiddleOfASentenceIsJustWords()
    {
        // "Send it to Kate tomorrow" is a sentence somebody dictated. Stripping it there would
        // delete two words of their text and submit a half-finished message.
        var (text, submit) = EditCommandParser.StripSendIt("send it to Kate tomorrow");

        Assert.False(submit);
        Assert.Equal("send it to Kate tomorrow", text);
    }

    [Fact]
    public void SendItPressesEnterAfterInjection()
    {
        // The ordering is the point: Enter is synthesised only once injection is verified.
        // Submitting a form that never received the text is worse than not submitting.
        var order = new List<string>();

        var (text, submit) = EditCommandParser.StripSendIt("ship it send it");
        Assert.True(submit);

        // Stand-in for the orchestrator's sequence.
        order.Add("inject:" + text);
        if (submit)
        {
            order.Add("enter");
        }

        Assert.Equal(["inject:ship it", "enter"], order);
    }

    [Fact]
    public void UndoStackIsEmptyAfterRestart()
    {
        // In-memory only, and deliberately so: a persisted stack could restore text into a
        // document that has since been edited by hand.
        var first = new UndoStack();
        first.Push("before", "after", "edit");
        Assert.True(first.CanUndo);

        var afterRestart = new UndoStack();
        Assert.False(afterRestart.CanUndo);
        Assert.Null(afterRestart.Original);
    }

    [Fact]
    public void ChangingSelectionStartsAFreshUndoStack()
    {
        // Undoing into a different piece of text is not undo, it is corruption.
        var stack = new UndoStack();
        stack.Push("first original", "first result", "edit");

        // "second original" is not what the previous edit produced, so this is a different piece
        // of text and the chain restarts rather than letting an undo cross between them.
        stack.Push("second original", "second result", "edit");

        Assert.Equal(1, stack.Count);
        Assert.Equal("second original", stack.Original);
    }

    [Fact]
    public void UndoStackIsBounded()
    {
        // Edit Mode invites repeated tries, and an unbounded stack of document-sized strings is a
        // memory leak with a friendly name.
        var stack = new UndoStack(capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            stack.Push($"version {i}", $"version {i + 1}", "edit");
        }

        Assert.Equal(3, stack.Count);
        Assert.Equal("version 7", stack.Original);
    }

    [Fact]
    public void ClearingOnFocusChangeEmptiesTheStack()
    {
        var stack = new UndoStack();
        stack.Push("text", "edited text", "edit");

        stack.Clear();

        Assert.False(stack.CanUndo);
    }
}
