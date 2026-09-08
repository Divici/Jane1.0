using Jane.Windows.Automation;

namespace Jane.Windows.Injection;

/// <summary>
/// Makes sure Jane never walks away from a keyboard with a key held down.
/// </summary>
/// <remarks>
/// <para>
/// A held modifier is the worst state a dictation tool can leave behind, because it outlives the
/// dictation and it is invisible: nothing on screen says "Ctrl is down", the user simply finds
/// that typing has stopped producing letters. It survives switching windows. On many machines the
/// only fix somebody finds is a reboot.
/// </para>
/// <para>
/// Two ways in. The clipboard strategy presses Ctrl itself, and <c>SendInput</c> is allowed to
/// accept some records and refuse the rest -- so a chord can go out as a down with no matching up.
/// And a user can be holding a modifier of their own that outlasted the gate's timeout. The first
/// is Jane's fault and is fixed at the source; the second is not, and is fixed here anyway,
/// because from the outside they are the same broken keyboard.
/// </para>
/// </remarks>
internal static class ModifierRelease
{
    /// <summary>
    /// Releases every modifier still physically down, and names them.
    /// </summary>
    /// <returns>
    /// What was found and released, joined for display, or null when the keyboard was clean --
    /// which is the ordinary case, and costs one <c>GetAsyncKeyState</c> call per modifier.
    /// </returns>
    public static string? ReleaseHeldModifiers(IAsyncKeyState keyState, ISendInput sendInput)
    {
        List<int>? held = null;

        foreach (var virtualKey in ModifierGate.ModifierVirtualKeys)
        {
            if (keyState.IsPhysicallyDown(virtualKey))
            {
                (held ??= []).Add(virtualKey);
            }
        }

        if (held is null)
        {
            return null;
        }

        // Sided and aggregate virtual keys both appear in the list, and releasing an aggregate
        // that no sided key backs is harmless -- Windows resolves it to nothing. Sending too many
        // ups is safe; sending too few is the bug.
        var records = new InputRecord[held.Count];
        for (var i = 0; i < held.Count; i++)
        {
            records[i] = InputRecord.VirtualKey(held[i], keyUp: true);
        }

        try
        {
            sendInput.Send(records);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Nothing further to try, and this runs on the way out of an injection that has
            // already reported its own outcome. The names still go on the result.
        }

        return string.Join(" + ", held.Select(VirtualKeys.NameOf));
    }
}
