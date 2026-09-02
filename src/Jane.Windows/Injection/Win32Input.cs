using System.Runtime.InteropServices;

namespace Jane.Windows.Injection;

/// <summary>Win32 virtual-key codes Jane synthesises or interrogates.</summary>
/// <remarks>
/// Both the sided and the aggregate modifier codes are listed. Windows normally sets the
/// aggregate (<see cref="Control"/>) whenever a side is down, so polling the sides alone would
/// be enough on real hardware -- but remappers, KVMs and other injectors can leave the aggregate
/// set on its own, and the cost of reading three more keys is a P/Invoke each. Given that a
/// missed held Ctrl means executing control chords in a terminal, the gate reads all of them.
/// </remarks>
public static class VirtualKeys
{
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int Return = 0x0D;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftAlt = 0xA4;
    public const int RightAlt = 0xA5;
    public const int LeftWindows = 0x5B;
    public const int RightWindows = 0x5C;
    public const int KeyV = 0x56;

    /// <summary>A human name for a virtual key, for the overlay message that follows an abort.</summary>
    public static string NameOf(int virtualKey) => virtualKey switch
    {
        Shift => "Shift",
        Control => "Ctrl",
        Alt => "Alt",
        LeftShift => "Left Shift",
        RightShift => "Right Shift",
        LeftControl => "Left Ctrl",
        RightControl => "Right Ctrl",
        LeftAlt => "Left Alt",
        RightAlt => "Right Alt",
        LeftWindows => "Left Win",
        RightWindows => "Right Win",
        _ => $"VK 0x{virtualKey:X2}",
    };
}

/// <summary>Flags for the <c>dwFlags</c> field of <c>KEYBDINPUT</c>.</summary>
public static class KeyEventFlags
{
    public const uint ExtendedKey = 0x0001;
    public const uint KeyUp = 0x0002;

    /// <summary>
    /// The scan-code field carries a UTF-16 code unit instead of a scan code, and the virtual
    /// key must be zero. This is what preserves non-ASCII text and emoji: Windows turns each
    /// code unit into a WM_CHAR without consulting the keyboard layout.
    /// </summary>
    public const uint Unicode = 0x0004;

    public const uint ScanCode = 0x0008;
}

/// <summary>The keyboard arm of the Win32 <c>INPUT</c> union.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>
/// One Win32 <c>INPUT</c> record, laid out for x64.
/// </summary>
/// <remarks>
/// <c>INPUT</c> is a tagged union whose size is set by its largest arm, <c>MOUSEINPUT</c>
/// (32 bytes on x64), not by the keyboard arm Jane uses (24 bytes). <c>SendInput</c> validates
/// <c>cbSize</c> against the full union and returns zero if it disagrees, so the record is
/// declared at the full 40 bytes with the union starting at its natural 8-byte alignment.
/// Jane publishes win-x64 only; <see cref="Win32SendInput.RecordSize"/> is asserted in tests.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct InputRecord
{
    /// <summary>INPUT_KEYBOARD is 1. Jane never synthesises mouse or hardware input.</summary>
    public const uint TypeKeyboard = 1;

    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(8)]
    public KeyboardInput Keyboard;

    /// <summary>A UTF-16 code unit delivered verbatim, with no virtual key and no layout lookup.</summary>
    public static InputRecord UnicodeCodeUnit(char codeUnit, bool keyUp) => new()
    {
        Type = TypeKeyboard,
        Keyboard = new KeyboardInput
        {
            VirtualKey = 0,
            ScanCode = codeUnit,
            Flags = KeyEventFlags.Unicode | (keyUp ? KeyEventFlags.KeyUp : 0),
        },
    };

    /// <summary>A real key press, used for Enter and for the Ctrl+V of the clipboard strategy.</summary>
    public static InputRecord VirtualKey(int virtualKey, bool keyUp) => new()
    {
        Type = TypeKeyboard,
        Keyboard = new KeyboardInput
        {
            VirtualKey = (ushort)virtualKey,
            ScanCode = 0,
            Flags = keyUp ? KeyEventFlags.KeyUp : 0,
        },
    };
}

/// <param name="Accepted">How many records <c>SendInput</c> accepted into the input stream.</param>
/// <param name="LastError">
/// <c>GetLastError</c> when fewer records were accepted than were offered. ERROR_ACCESS_DENIED (5)
/// is UIPI refusing to type into a more-privileged window, which is a distinct, explainable
/// failure rather than an unknown one.
/// </param>
public readonly record struct SendInputOutcome(uint Accepted, int LastError)
{
    public const int ErrorAccessDenied = 5;
}

/// <summary>Seam over <c>SendInput</c>, so injection can be asserted without a real keyboard.</summary>
/// <remarks>
/// Tests substitute a fake that records the decoded <c>INPUT</c> array. Reconstructing the
/// string from the recorded records is a stricter check than reading it back out of a real
/// Notepad, and it runs headless without keystrokes escaping into the session.
/// </remarks>
public interface ISendInput
{
    SendInputOutcome Send(ReadOnlySpan<InputRecord> inputs);
}

/// <summary>Seam over <c>GetAsyncKeyState</c> -- the physical state of a key, right now.</summary>
public interface IAsyncKeyState
{
    /// <summary>True while the key is physically down, independent of which window has focus.</summary>
    bool IsPhysicallyDown(int virtualKey);
}

/// <inheritdoc cref="ISendInput"/>
public sealed unsafe partial class Win32SendInput : ISendInput
{
    /// <summary>Marshalled size of <c>INPUT</c> on x64. Passed as <c>cbSize</c>.</summary>
    public static int RecordSize => sizeof(InputRecord);

    public SendInputOutcome Send(ReadOnlySpan<InputRecord> inputs)
    {
        if (inputs.IsEmpty)
        {
            return new SendInputOutcome(0, 0);
        }

        fixed (InputRecord* first = inputs)
        {
            var accepted = SendInput((uint)inputs.Length, first, sizeof(InputRecord));
            var lastError = accepted == (uint)inputs.Length ? 0 : Marshal.GetLastWin32Error();
            return new SendInputOutcome(accepted, lastError);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, InputRecord* inputs, int recordSize);
}

/// <inheritdoc cref="IAsyncKeyState"/>
public sealed partial class Win32AsyncKeyState : IAsyncKeyState
{
    public bool IsPhysicallyDown(int virtualKey) =>
        // The high bit is "down now"; the low bit is "pressed since the last call" and is
        // deliberately ignored -- a key that was tapped and released is not a hazard.
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
