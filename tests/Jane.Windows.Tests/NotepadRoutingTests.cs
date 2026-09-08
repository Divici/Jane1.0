using Jane.Core.Abstractions;
using Jane.Windows.Hotkeys;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// Notepad types, and a key Jane hides stays hidden in both directions.
/// </summary>
/// <remarks>
/// Both of these come out of the same field report: a long dictation into Notepad arrived as
/// something other than the text, and afterwards the keyboard itself misbehaved. The first is
/// addressed by taking Notepad off the clipboard path entirely; the second by making sure Jane
/// never delivers half of a key event to an application.
/// </remarks>
public sealed class NotepadRoutingTests
{
    [Theory]
    [InlineData("notepad")]
    [InlineData("Notepad")]
    public void NotepadTypesRatherThanPastesHoweverLongTheTextIs(string processName)
    {
        // 200 characters was the threshold that sent everything longer through the clipboard, and
        // the reported dictation was well past it.
        var selector = new InjectionStrategySelector();
        var target = new TargetWindow(1, 2, processName, "Notepad", "Untitled");

        var decision = selector.Select(target, characterCount: 4_000);

        Assert.Equal(InjectionStrategy.Unicode, decision.Strategy);
        Assert.Contains("Plain-text", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARichTextTargetStillPastes()
    {
        // The clipboard path is not being retired -- Electron, Chromium and Office all need it,
        // for reasons the selector states on each rule.
        var selector = new InjectionStrategySelector();
        var target = new TargetWindow(1, 2, "code", "Chrome_WidgetWin_1", "main.cs - VS Code");

        Assert.Equal(InjectionStrategy.Clipboard, selector.Select(target, characterCount: 4_000).Strategy);
    }

    [Fact]
    public void AnEscapeJaneHidesIsHiddenInBothDirections()
    {
        // An application that receives a key-up it never saw pressed may do anything with it.
        // Cancelling clears the active flag between the two events, so a rule that only read the
        // current state swallowed the down and delivered the up.
        using var hook = new LowLevelKeyboardHook();
        hook.NotifyPipelineActive(true);

        Assert.True(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, timestamp: 0));

        // The cancel lands here: by the time the key comes up there is no dictation any more.
        hook.NotifyPipelineActive(false);

        Assert.True(
            hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, timestamp: 1),
            "The key-up matching a swallowed key-down must be swallowed too.");
    }

    [Fact]
    public void AnEscapeJaneLetsThroughKeepsItsKeyUp()
    {
        using var hook = new LowLevelKeyboardHook();

        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, timestamp: 0));
        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, timestamp: 1));
    }

    [Fact]
    public void AStrayEscapeKeyUpIsDelivered()
    {
        // A key already held when Jane started has a down the hook never saw. Eating its up would
        // hide a key press Jane had nothing to do with.
        using var hook = new LowLevelKeyboardHook();
        hook.NotifyPipelineActive(true);

        Assert.True(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: true, timestamp: 0));
        Assert.True(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, timestamp: 1));

        // Second up, no down of its own.
        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkEscape, isKeyDown: false, timestamp: 2));
    }

    [Fact]
    public void TheBoundKeyIsStillNeverSwallowedInEitherDirection()
    {
        // Right Ctrl reaching every other app is the rule the whole hook is built around: push to
        // talk in a voice chat has to keep working while Jane is listening.
        using var hook = new LowLevelKeyboardHook();
        hook.NotifyPipelineActive(true);

        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkRightControl, isKeyDown: true, timestamp: 0));
        Assert.False(hook.RecordHookEvent(HotkeyBinding.VkRightControl, isKeyDown: false, timestamp: 1));
    }
}
