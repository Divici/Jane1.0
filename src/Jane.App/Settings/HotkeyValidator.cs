using Jane.App.Controls;
using Jane.Core.Abstractions;

namespace Jane.App.Settings;

/// <summary>What Jane thinks of a proposed hotkey.</summary>
public enum HotkeyVerdict
{
    /// <summary>Nothing to say. Bind it.</summary>
    Accepted,

    /// <summary>Bindable, but the user should know something before they commit to it.</summary>
    Warned,

    /// <summary>Refused. Either Windows will never deliver it, or binding it would break typing.</summary>
    Rejected,
}

/// <param name="Message">Shown under the picker. Written to be read, not to be parsed.</param>
public sealed record HotkeyValidation(HotkeyVerdict Verdict, string Message)
{
    public static HotkeyValidation Ok(string message) => new(HotkeyVerdict.Accepted, message);

    /// <summary>Whether the binding may be saved. A warning may; a rejection may not.</summary>
    public bool IsAcceptable => Verdict != HotkeyVerdict.Rejected;

    public Severity Severity => Verdict switch
    {
        HotkeyVerdict.Rejected => Severity.Error,
        HotkeyVerdict.Warned => Severity.Warning,
        _ => Severity.Success,
    };
}

/// <summary>
/// Decides whether a proposed hotkey is one Jane can actually have.
/// </summary>
/// <remarks>
/// <para>
/// Three separate reasons a combination can be refused, and they are genuinely different:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Windows will not deliver it.</b> Ctrl+Alt+Delete goes to the Secure Attention Sequence and
/// never reaches a low-level hook at all; Ctrl+Shift+Esc and the Win+ shortcuts are handled by
/// the shell first. Binding one of these produces a hotkey that silently never fires, which is
/// the worst failure mode a settings screen can ship.
/// </description></item>
/// <item><description>
/// <b>Binding it would break typing.</b> A bare printable key means Jane swallows every press of
/// it in every application. So does bare Enter, Tab or Backspace.
/// </description></item>
/// <item><description>
/// <b>Jane already uses it.</b> Esc cancels a dictation in flight, and Ctrl+V is synthesised by
/// the clipboard injection strategy -- a hotkey that fires while Jane is pasting is a loop.
/// </description></item>
/// </list>
/// <para>
/// Warnings are a separate axis and are never fatal. Right Ctrl -- Jane's own default -- is the
/// canonical case: it is a fine hotkey and it is also the single most common push-to-talk bind in
/// games and voice chat, and the plan requires the user to be told so at the moment they choose
/// it rather than the first time a game eats it.
/// </para>
/// </remarks>
public static class HotkeyValidator
{
    /// <summary>Combinations the shell or the secure desktop takes before any hook sees them.</summary>
    private static readonly (int Key, int[] Modifiers, string Name, string Why)[] SystemReserved =
    [
        (KeyNames.VkDelete, [KeyNames.VkControl, KeyNames.VkAlt], "Ctrl+Alt+Delete",
            "Windows routes it to the Secure Attention Sequence, which no application can intercept."),
        (KeyNames.VkEscape, [KeyNames.VkControl, KeyNames.VkShift], "Ctrl+Shift+Esc",
            "the shell opens Task Manager before any application sees the keystroke."),
        (KeyNames.VkTab, [KeyNames.VkAlt], "Alt+Tab",
            "the shell's window switcher takes it first."),
        (KeyNames.VkEscape, [KeyNames.VkAlt], "Alt+Esc",
            "the shell uses it to cycle windows."),
        (KeyNames.VkTab, [KeyNames.VkControl, KeyNames.VkAlt], "Ctrl+Alt+Tab",
            "the shell's window switcher takes it first."),
        (0x73, [KeyNames.VkAlt], "Alt+F4",
            "Windows closes the focused window before the keystroke reaches Jane."),
        (KeyNames.VkTab, [KeyNames.VkLeftWindows], "Win+Tab",
            "the shell opens Task View before any application sees it."),
    ];

    /// <summary>
    /// Keys the shell claims whenever the Windows key is held. Not exhaustive, and does not need
    /// to be: any Win+ combination is a shell shortcut by convention, so the whole class is
    /// refused with one message rather than enumerated badly.
    /// </summary>
    private static bool UsesWindowsKey(HotkeyBinding binding) =>
        binding.VirtualKey is KeyNames.VkLeftWindows or KeyNames.VkRightWindows ||
        binding.RequiresModifiers.Any(m => m is KeyNames.VkLeftWindows or KeyNames.VkRightWindows);

    /// <summary>Keys Jane itself is already using, in the same process, at the same moment.</summary>
    private static readonly Dictionary<int, string> JaneReserved = new()
    {
        [KeyNames.VkEscape] = "Esc cancels a dictation that is already running, so Jane consumes it while the pipeline is active.",
    };

    /// <summary>
    /// Common push-to-talk and game binds. Warned about, never refused -- Right Ctrl is Jane's
    /// own default, and refusing the default would be absurd.
    /// </summary>
    private static readonly Dictionary<int, string> GameBinds = new()
    {
        [KeyNames.VkRightControl] = "Right Ctrl is the most common push-to-talk bind in games and voice chat. If a game claims it, use the tray's Pause item while you play, or bind Jane to something else.",
        [KeyNames.VkRightAlt] = "Right Alt is a frequent game bind, and on international keyboard layouts it is AltGr -- holding it there changes what your other keys type.",
        [KeyNames.VkRightShift] = "Right Shift is a common game bind, and holding Shift while Jane injects text can capitalise it in some applications.",
        [KeyNames.VkCapsLock] = "Caps Lock is a toggle: Windows flips the lock state on every press, so holding it to talk also turns capitals on and off.",
    };

    /// <summary>
    /// Judges a proposed binding.
    /// </summary>
    /// <remarks>
    /// Order matters. A refusal is checked before a warning, because a combination that will
    /// never fire is not improved by also being a popular game bind.
    /// </remarks>
    public static HotkeyValidation Validate(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var name = KeyNames.Describe(binding);

        foreach (var (key, modifiers, reserved, why) in SystemReserved)
        {
            if (binding.VirtualKey == key && SameModifiers(binding.RequiresModifiers, modifiers))
            {
                return new HotkeyValidation(
                    HotkeyVerdict.Rejected,
                    $"{reserved} cannot be used: {why}");
            }
        }

        if (UsesWindowsKey(binding))
        {
            return new HotkeyValidation(
                HotkeyVerdict.Rejected,
                "The Windows key is reserved by the shell. Combinations that use it open Start, File Explorer, or the lock screen before Jane ever sees them.");
        }

        if (JaneReserved.TryGetValue(binding.VirtualKey, out var mine) && binding.RequiresModifiers.Count == 0)
        {
            return new HotkeyValidation(HotkeyVerdict.Rejected, $"{name} is already Jane's. {mine}");
        }

        if (IsUniversalEditCommand(binding))
        {
            return new HotkeyValidation(
                HotkeyVerdict.Rejected,
                $"{name} is a standard editing shortcut, and Jane's clipboard injection strategy synthesises Ctrl+V itself. Binding it would have Jane trigger itself mid-paste.");
        }

        if (binding.RequiresModifiers.Count == 0 && KeyNames.IsPrintable(binding.VirtualKey))
        {
            return new HotkeyValidation(
                HotkeyVerdict.Rejected,
                $"{name} types a character. Bound on its own, Jane would swallow every press of it in every application -- add a modifier, or pick a key that does not produce text.");
        }

        if (binding.RequiresModifiers.Count == 0 &&
            binding.VirtualKey is KeyNames.VkReturn or KeyNames.VkTab or KeyNames.VkBack)
        {
            return new HotkeyValidation(
                HotkeyVerdict.Rejected,
                $"{name} is needed for basic keyboard navigation and text entry. Add a modifier, or pick a key that is not.");
        }

        if (GameBinds.TryGetValue(binding.VirtualKey, out var warning) && binding.RequiresModifiers.Count == 0)
        {
            return new HotkeyValidation(HotkeyVerdict.Warned, warning);
        }

        if (binding.RequiresModifiers.Count == 0 && binding.VirtualKey is >= 0x70 and <= 0x7B)
        {
            return new HotkeyValidation(
                HotkeyVerdict.Warned,
                $"{name} is bound by many applications on its own -- rename, help, refresh, and debugger controls all live on the function row. It will work, but it may be taken in the app you are dictating into.");
        }

        return HotkeyValidation.Ok(
            binding.RequiresModifiers.Count == 0 && KeyNames.IsModifier(binding.VirtualKey)
                ? $"{name} is free to hold. A bare modifier is the best kind of push-to-talk key -- it types nothing on its own."
                : $"{name} is available.");
    }

    /// <summary>Ctrl+C/V/X/Z/Y/A, which every text control in Windows already owns.</summary>
    private static bool IsUniversalEditCommand(HotkeyBinding binding)
    {
        var hasControl = binding.RequiresModifiers.Any(m =>
            m is KeyNames.VkControl or KeyNames.VkLeftControl or KeyNames.VkRightControl);

        return hasControl &&
               binding.RequiresModifiers.Count == 1 &&
               binding.VirtualKey is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A;
    }

    /// <summary>
    /// Modifier comparison that treats the sided keys as the generic one.
    /// </summary>
    /// <remarks>
    /// A hook reports <c>VK_LCONTROL</c>; a person configuring "Ctrl+Alt+Delete" means
    /// <c>VK_CONTROL</c>. Comparing them literally would let the reserved list be bypassed by
    /// pressing the other Ctrl, which is not a distinction Windows makes when it claims the
    /// combination.
    /// </remarks>
    private static bool SameModifiers(IReadOnlyList<int> actual, IReadOnlyList<int> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        var left = actual.Select(Generalise).OrderBy(v => v);
        var right = expected.Select(Generalise).OrderBy(v => v);
        return left.SequenceEqual(right);
    }

    private static int Generalise(int virtualKey) => virtualKey switch
    {
        KeyNames.VkLeftControl or KeyNames.VkRightControl => KeyNames.VkControl,
        KeyNames.VkLeftAlt or KeyNames.VkRightAlt => KeyNames.VkAlt,
        KeyNames.VkLeftShift or KeyNames.VkRightShift => KeyNames.VkShift,
        KeyNames.VkRightWindows => KeyNames.VkLeftWindows,
        _ => virtualKey,
    };
}
