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

/// <summary>
/// What the strategy measured on the way through. Diagnostics only -- nothing branches on it.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the field report of 2026-09-08 could not be told apart from its own
/// alternatives. Text arrived as a run of dots and every later keystroke did the same, which is
/// the signature of a modifier left held -- and also the signature of a clipboard restored before
/// the target finished reading it. On screen they are identical. Here they are not: one shows a
/// non-empty <see cref="ModifiersStillHeld"/>, the other a <see cref="PasteSettle"/> that hit its
/// ceiling.
/// </para>
/// <para>
/// Every field is optional and every default means "this strategy does not measure that", which
/// is why the nullable ones are nullable rather than zero: a <see cref="ClipboardRestored"/> of
/// <c>false</c> is a bug and a <c>null</c> is the Unicode path.
/// </para>
/// </remarks>
/// <param name="RecordsSent">INPUT records handed to SendInput, summed over every batch.</param>
/// <param name="RecordsAccepted">How many of those Windows took. Anything less is a partial injection.</param>
public sealed record InjectionDiagnostics
{
    public int RecordsSent { get; init; }

    public int RecordsAccepted { get; init; }

    /// <summary>What was physically down when the modifier gate first looked. Null means it did not run.</summary>
    public string? ModifiersInitiallyHeld { get; init; }

    /// <summary>What was still down when the gate gave up. Null on every healthy injection.</summary>
    public string? ModifiersStillHeld { get; init; }

    public TimeSpan ModifierWait { get; init; }

    /// <summary>How long the clipboard strategy waited after Ctrl+V before restoring.</summary>
    public TimeSpan PasteSettle { get; init; }

    /// <summary>Which condition ended the settle wait, so a ceiling is distinguishable from a confirmation.</summary>
    public string? PasteSettleReason { get; init; }

    /// <summary>Whether the user's clipboard came back. Null on the Unicode path, which never took it.</summary>
    public bool? ClipboardRestored { get; init; }

    public static InjectionDiagnostics Empty { get; } = new();
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
    /// <summary>What the strategy measured. Never null; <see cref="InjectionDiagnostics.Empty"/> when unmeasured.</summary>
    public InjectionDiagnostics Diagnostics { get; init; } = InjectionDiagnostics.Empty;

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
