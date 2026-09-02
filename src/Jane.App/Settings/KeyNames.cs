using System.Globalization;
using Jane.Core.Abstractions;

namespace Jane.App.Settings;

/// <summary>
/// Turns Win32 virtual-key codes into the names people actually use for them.
/// </summary>
/// <remarks>
/// <para>
/// Jane's hotkey is a raw VK code because it is read from a <c>WH_KEYBOARD_LL</c> hook, which is
/// the only way to observe a bare modifier being <em>held</em>. That leaves the settings window
/// holding <c>0xA3</c> and needing to say "Right Ctrl", and the difference between left and
/// right matters here more than it does in most apps: Right Ctrl is the default, and telling
/// somebody they have bound "Ctrl" when they have bound only one of them would be a lie that
/// costs them a dictation.
/// </para>
/// <para>
/// <c>GetKeyNameText</c> is deliberately not used. It returns the name of the key in the current
/// keyboard layout, which is right for a text editor and wrong here -- the hook reports a
/// layout-independent VK, and a name that changes when the user switches to a German layout
/// would not match what the hook is actually listening for.
/// </para>
/// </remarks>
public static class KeyNames
{
    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkPause = 0x13;
    public const int VkCapsLock = 0x14;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkPrintScreen = 0x2C;
    public const int VkInsert = 0x2D;
    public const int VkDelete = 0x2E;
    public const int VkLeftWindows = 0x5B;
    public const int VkRightWindows = 0x5C;
    public const int VkApps = 0x5D;
    public const int VkNumLock = 0x90;
    public const int VkScrollLock = 0x91;
    public const int VkLeftShift = 0xA0;
    public const int VkRightShift = 0xA1;
    public const int VkLeftControl = 0xA2;
    public const int VkRightControl = HotkeyBinding.VkRightControl;
    public const int VkLeftAlt = 0xA4;
    public const int VkRightAlt = 0xA5;

    private static readonly Dictionary<int, string> Named = new()
    {
        [VkBack] = "Backspace",
        [VkTab] = "Tab",
        [VkReturn] = "Enter",
        [VkShift] = "Shift",
        [VkControl] = "Ctrl",
        [VkAlt] = "Alt",
        [VkPause] = "Pause",
        [VkCapsLock] = "Caps Lock",
        [VkEscape] = "Esc",
        [VkSpace] = "Space",
        [VkPageUp] = "Page Up",
        [VkPageDown] = "Page Down",
        [VkEnd] = "End",
        [VkHome] = "Home",
        [VkLeft] = "Left Arrow",
        [VkUp] = "Up Arrow",
        [VkRight] = "Right Arrow",
        [VkDown] = "Down Arrow",
        [VkPrintScreen] = "Print Screen",
        [VkInsert] = "Insert",
        [VkDelete] = "Delete",
        [VkLeftWindows] = "Left Windows",
        [VkRightWindows] = "Right Windows",
        [VkApps] = "Menu",
        [VkNumLock] = "Num Lock",
        [VkScrollLock] = "Scroll Lock",
        [VkLeftShift] = "Left Shift",
        [VkRightShift] = "Right Shift",
        [VkLeftControl] = "Left Ctrl",
        [VkRightControl] = "Right Ctrl",
        [VkLeftAlt] = "Left Alt",
        [VkRightAlt] = "Right Alt",
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    /// <summary>The name of one key, e.g. <c>0xA3</c> becomes "Right Ctrl".</summary>
    public static string Of(int virtualKey)
    {
        if (Named.TryGetValue(virtualKey, out var name))
        {
            return name;
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return "Numpad " + (char)('0' + (virtualKey - 0x60));
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
        }

        return "Key 0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>The whole binding, e.g. "Ctrl + Shift + D" or, for the default, "Right Ctrl".</summary>
    public static string Describe(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var parts = binding.RequiresModifiers
            .Select(Of)
            .Append(Of(binding.VirtualKey));

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Whether the key produces a character when typed on its own.
    /// </summary>
    /// <remarks>
    /// This is what makes "bind a bare letter" a rejection rather than a warning: a bare
    /// printable key is one the user needs in order to write, and Jane would swallow every press
    /// of it everywhere.
    /// </remarks>
    public static bool IsPrintable(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39 ||
        virtualKey is >= 0x41 and <= 0x5A ||
        virtualKey is >= 0x60 and <= 0x6F ||
        virtualKey is >= 0xBA and <= 0xC0 ||
        virtualKey is >= 0xDB and <= 0xDE ||
        virtualKey == VkSpace;

    /// <summary>Whether the key is a modifier, which Jane alone is willing to bind on its own.</summary>
    public static bool IsModifier(int virtualKey) => virtualKey
        is VkShift or VkControl or VkAlt
        or VkLeftShift or VkRightShift
        or VkLeftControl or VkRightControl
        or VkLeftAlt or VkRightAlt;
}
