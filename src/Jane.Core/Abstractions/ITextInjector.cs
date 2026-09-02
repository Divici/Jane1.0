namespace Jane.Core.Abstractions;

/// <summary>How text is delivered to the focused control.</summary>
public enum InjectionStrategy
{
    /// <summary>SendInput with KEYEVENTF_UNICODE. Preserves non-ASCII; fragile in some terminals.</summary>
    Unicode,

    /// <summary>Save clipboard, set text, synthesise Ctrl+V, restore. More reliable for long text.</summary>
    Clipboard,
}

/// <summary>Why an injection did not happen. Every one of these has a designed overlay message.</summary>
public enum InjectionFailure
{
    None,

    /// <summary>Focus moved between key-down and injection. Typing here would leak into the wrong app.</summary>
    TargetChanged,

    /// <summary>The captured window no longer exists.</summary>
    TargetGone,

    /// <summary>
    /// A modifier was still physically held when the wait expired. Injecting into a held-Ctrl
    /// state turns text into control chords, which in a terminal is execution, not misplacement.
    /// </summary>
    ModifierHeld,

    /// <summary>The clipboard could not be read or restored, so the strategy was abandoned.</summary>
    ClipboardUnavailable,

    /// <summary>UIPI blocked the input -- an elevated target and no uiAccess.</summary>
    PrivilegeBlocked,

    Unknown,
}

/// <param name="CharactersSent">What actually went out, for history and the eval corpus.</param>
public sealed record InjectionResult(
    bool Succeeded,
    InjectionStrategy Strategy,
    int CharactersSent,
    TimeSpan Elapsed,
    InjectionFailure Failure = InjectionFailure.None,
    string? Detail = null)
{
    public static InjectionResult Aborted(InjectionFailure failure, string detail) =>
        new(false, InjectionStrategy.Unicode, 0, TimeSpan.Zero, failure, detail);
}

/// <summary>
/// Identity of the window that had focus at key-down.
/// </summary>
/// <remarks>
/// Captured at key-down and re-verified immediately before injection. HWNDs are recycled, so the
/// handle alone is not identity -- the process id and name are what make a stale match detectable.
/// </remarks>
/// <param name="ProcessName">Lower-case, no extension. The key for per-app strategy and instructions.</param>
public sealed record TargetWindow(
    nint Handle,
    int ProcessId,
    string ProcessName,
    string WindowClass,
    string WindowTitle)
{
    public static TargetWindow None { get; } = new(0, 0, string.Empty, string.Empty, string.Empty);

    public bool IsNone => Handle == 0;

    /// <summary>
    /// Same window, same process -- title deliberately excluded, since a title legitimately
    /// changes while the user dictates (an editor marking the document dirty, for instance).
    /// </summary>
    public bool MatchesIdentity(TargetWindow other) =>
        Handle == other.Handle && ProcessId == other.ProcessId &&
        string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase);
}

public interface ITextInjector
{
    Task<InjectionResult> InjectAsync(string text, TargetWindow target, CancellationToken cancellationToken);
}

/// <summary>Reads the currently focused window's identity.</summary>
public interface IFocusTracker
{
    TargetWindow GetForegroundWindow();
}
