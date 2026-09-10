using System.Diagnostics;
using System.Text;
using Jane.Core.Abstractions;
using Jane.Windows.Automation;

namespace Jane.Windows.Injection;

/// <param name="PasteSettleDelay">
/// How long to leave Jane's text on the clipboard after Ctrl+V before putting the user's own
/// contents back. The target reads the clipboard when it processes the key message, which
/// happens after <c>SendInput</c> returns, so restoring immediately would race the paste and
/// the user would get their old clipboard contents instead of their dictation.
/// </param>
/// <param name="PasteSettleCeiling">
/// How long Jane leaves its text on the clipboard before putting the user's back. Zero disables
/// the wait entirely and falls back to <paramref name="PasteSettleDelay"/> alone, which is what
/// the tests use.
/// </param>
/// <param name="PasteSettlePoll">Unused. Kept so an explicitly-configured caller still compiles.</param>
public sealed record ClipboardInjectorOptions(
    TimeSpan PasteSettleDelay,
    TimeSpan PasteSettleCeiling = default,
    TimeSpan PasteSettlePoll = default)
{
    /// <summary>
    /// 750 ms, which is a wait rather than a measurement, and says so.
    /// </summary>
    /// <remarks>
    /// The first version was a flat 60 ms, guessed. The second polled the clipboard sequence
    /// number for a confirmation that could never arrive, because that number tracks contents
    /// changing and a read changes nothing -- so it always ran the full ceiling while claiming to
    /// be watching for something. This is the same 750 ms without the claim.
    /// <para>
    /// Generous on purpose: overshooting means the user's clipboard comes back a little late, and
    /// undershooting means their old clipboard lands in the document instead of the dictation.
    /// </para>
    /// </remarks>
    public static ClipboardInjectorOptions Default { get; } = new(
        PasteSettleDelay: TimeSpan.FromMilliseconds(30),
        PasteSettleCeiling: TimeSpan.FromMilliseconds(750),
        PasteSettlePoll: TimeSpan.FromMilliseconds(10));
}

/// <summary>
/// Puts long text in via the clipboard: save what is there, set the text, Ctrl+V, put it back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The clipboard belongs to the user.</strong> Jane borrows it for a few tens of
/// milliseconds and hands it back. The restore runs in a <c>finally</c>, so a failure anywhere
/// between borrowing and pasting -- including one thrown from deep inside <c>SendInput</c> --
/// still returns the user's copied text. Losing a dictation is an annoyance; silently losing
/// something the user copied twenty minutes ago is worse.
/// </para>
/// <para>
/// <strong>Restoration is best-effort, deliberately.</strong> Only memory-backed text formats
/// are saved. Reading an arbitrary clipboard format can force the source app to render it
/// synchronously and stall or hang, so Jane never walks the whole format list reading data --
/// see <see cref="Win32Clipboard"/>.
/// </para>
/// <para>
/// <strong>Clipboard history caveat.</strong> Anything that passes through the clipboard can be
/// captured by a clipboard manager. Jane marks its payload with the three formats Windows
/// honours to keep content out of Win+V history and out of cross-device cloud sync, so its own
/// text does not pile up there. Third-party clipboard managers are not obliged to honour those
/// markers, so a user running one should prefer the Unicode strategy; settings says so, and the
/// per-app strategy override is how they force it.
/// </para>
/// </remarks>
public sealed class ClipboardInjector : ITextInjector
{
    private readonly InjectionPreflight _preflight;
    private readonly IClipboard _clipboard;
    private readonly ISendInput _sendInput;
    private readonly IAsyncKeyState _keyState;
    private readonly ClipboardInjectorOptions _options;
    private readonly uint[] _restorableFormats;
    private readonly uint[] _historyOptOutFormats;

    public ClipboardInjector(
        IFocusTracker focusTracker,
        IWindowLiveness liveness,
        IClipboard clipboard,
        ISendInput sendInput,
        ModifierGate modifierGate,
        ClipboardInjectorOptions? options = null)
    {
        _preflight = new InjectionPreflight(focusTracker, liveness, modifierGate);
        _clipboard = clipboard;
        _sendInput = sendInput;
        _keyState = modifierGate.KeyState;
        _options = options ?? ClipboardInjectorOptions.Default;

        // HTML and RTF have no fixed id -- they are registered names, so they are resolved once
        // here rather than looked up per dictation.
        _restorableFormats =
        [
            .. ClipboardFormats.RestorableStandardFormats,
            clipboard.RegisterFormat(ClipboardFormats.HtmlFormatName),
            clipboard.RegisterFormat(ClipboardFormats.RtfFormatName),
        ];

        _historyOptOutFormats = [.. ClipboardFormats.HistoryOptOutFormatNames.Select(clipboard.RegisterFormat)];
    }

    public async Task<InjectionResult> InjectAsync(
        string text, TargetWindow target, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        if (string.IsNullOrEmpty(text))
        {
            return new InjectionResult(true, InjectionStrategy.Clipboard, 0, Stopwatch.GetElapsedTime(startedAt));
        }

        var preflight = await _preflight.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        var diagnostics = preflight.Describe();

        if (preflight.Abort is { } reason)
        {
            return Failed(startedAt, reason.Failure, reason.Detail, diagnostics);
        }

        List<ClipboardPayload> saved;
        try
        {
            saved = Capture();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never guess. If the clipboard could not be read, it cannot be put back either,
            // and pasting over an unrecoverable clipboard is worse than not injecting.
            return Failed(startedAt, InjectionFailure.ClipboardUnavailable, ex.Message, diagnostics);
        }

        var restored = false;
        var settleStartedAt = Stopwatch.GetTimestamp();
        ClipboardSettle settle = default;

        try
        {
            _clipboard.SetContents(BuildPayload(text));

            // Sampled between Jane's own write and the paste, so anything that moves it afterwards
            // is another application writing to the clipboard, not the target reading from it.
            var baseline = _clipboard.SequenceNumber;

            SendPaste();
            settle = await WaitForPasteAsync(baseline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(startedAt, InjectionFailure.Unknown, ex.Message, diagnostics with
            {
                RecordsSent = PasteChordLength,
                PasteSettle = Stopwatch.GetElapsedTime(settleStartedAt),
                PasteSettleReason = "aborted",
                ModifiersStuckAfter = ModifierRelease.ReleaseHeldModifiers(_keyState, _sendInput),
            });
        }
        finally
        {
            // Skipped only when something else has already claimed the clipboard: putting the old
            // contents back over their write would lose it. On every other path the user gets
            // their clipboard back, including the ones that threw.
            restored = !settle.TakenOver && Restore(saved);
        }

        return new InjectionResult(
            true, InjectionStrategy.Clipboard, text.Length, Stopwatch.GetElapsedTime(startedAt))
        {
            Diagnostics = diagnostics with
            {
                RecordsSent = PasteChordLength,
                RecordsAccepted = PasteChordLength,
                PasteSettle = settle.Waited,
                PasteSettleReason = settle.Reason,
                ClipboardRestored = restored,
                ModifiersStuckAfter = ModifierRelease.ReleaseHeldModifiers(_keyState, _sendInput),
            },
        };
    }

    /// <param name="Reason">Why the wait ended, and whether anything took the clipboard meanwhile.</param>
    /// <param name="TakenOver">
    /// True when another application wrote to the clipboard while Jane was borrowing it.
    /// </param>
    /// <remarks>
    /// Phrased as "taken over" rather than "safe to restore" so that the default value of the
    /// struct -- which is what the aborting paths carry -- means restore. Getting the clipboard
    /// back to the user is the behaviour that must not depend on remembering to ask for it.
    /// </remarks>
    private readonly record struct ClipboardSettle(TimeSpan Waited, string Reason, bool TakenOver);

    /// <summary>
    /// Waits for the target to actually take the paste before the clipboard is handed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a flat 60 ms sleep, which is a race with a name. The target reads the
    /// clipboard when it processes the key message, which happens some time after
    /// <c>SendInput</c> returns -- and how long after is a property of that application's message
    /// loop, not of Jane. Windows 11's Notepad is a WinUI app that can take considerably longer
    /// than 60 ms under load, and restoring underneath it means the user's own clipboard is what
    /// gets pasted, or nothing at all.
    /// </para>
    /// <para>
    /// It was then rewritten to wait for evidence, polling the clipboard sequence number until it
    /// moved, on the belief that a target opening the clipboard to read a paste would move it. It
    /// does not: that number tracks clipboard <em>contents changing</em>, and a read changes
    /// nothing. The confirming branch was unreachable and every clipboard injection in the field
    /// log ended at the ceiling, which is the shape a mechanism has when it is not working.
    /// </para>
    /// <para>
    /// There is no cheap signal for "the paste landed". Windows will report it through delayed
    /// rendering -- publish the format with no data and it sends <c>WM_RENDERFORMAT</c> at the
    /// moment a consumer asks -- but that means owning the clipboard from a window with a live
    /// message pump and being certain to answer in time, where being late means the paste fails
    /// outright. That is a worse failure than waiting, so the wait is honest about being a wait.
    /// </para>
    /// <para>
    /// What the sequence number is genuinely good for is the one case where restoring does harm:
    /// if it moved, another application wrote to the clipboard while Jane was borrowing it, and
    /// putting the old contents back would discard whatever they had just copied.
    /// </para>
    /// </remarks>
    private async Task<ClipboardSettle> WaitForPasteAsync(uint before, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        // Zero means the caller asked for no settle at all, which only the tests do.
        if (_options.PasteSettleCeiling <= TimeSpan.Zero)
        {
            await Task.Delay(_options.PasteSettleDelay, cancellationToken).ConfigureAwait(false);
            return new ClipboardSettle(Stopwatch.GetElapsedTime(startedAt), "no-wait", TakenOver: false);
        }

        await Task.Delay(_options.PasteSettleCeiling, cancellationToken).ConfigureAwait(false);

        // Not a confirmation that the paste happened -- nothing reports that -- but a check for
        // the one case where restoring would do harm. If the number moved, some other application
        // wrote to the clipboard while Jane was borrowing it, and putting the old contents back
        // would throw away whatever they had just copied.
        var takenOver = before != 0 && _clipboard.SequenceNumber != before;

        return new ClipboardSettle(
            Stopwatch.GetElapsedTime(startedAt),
            takenOver ? "taken-over" : "elapsed",
            TakenOver: takenOver);
    }

    /// <summary>Ctrl down, V down, V up, Ctrl up.</summary>
    private const int PasteChordLength = 4;

    /// <summary>
    /// Saves only the allowlisted formats, and never asks for data on anything else.
    /// </summary>
    /// <remarks>
    /// The enumeration itself is safe -- it is the read that can force a delayed render -- so
    /// the list is walked, then intersected with the allowlist, and only the intersection is read.
    /// </remarks>
    private List<ClipboardPayload> Capture()
    {
        var available = _clipboard.GetAvailableFormats();
        List<ClipboardPayload> saved = [];

        foreach (var format in available)
        {
            if (!_restorableFormats.Contains(format))
            {
                continue;
            }

            var data = _clipboard.TryGetFormatData(format);
            if (data is not null)
            {
                saved.Add(new ClipboardPayload(format, data));
            }
        }

        return saved;
    }

    private List<ClipboardPayload> BuildPayload(string text)
    {
        // CF_UNICODETEXT is null-terminated by contract; a missing terminator is read as a
        // buffer overrun by some targets and shows up as trailing garbage.
        List<ClipboardPayload> payload =
        [
            new(ClipboardFormats.UnicodeText, Encoding.Unicode.GetBytes(text + '\0')),
        ];

        foreach (var format in _historyOptOutFormats)
        {
            payload.Add(new ClipboardPayload(format, BitConverter.GetBytes(0u)));
        }

        return payload;
    }

    /// <summary>
    /// Synthesises Ctrl+V, and guarantees the key-ups whatever happens to the key-downs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SendInput</c> is documented to stop at the first record another thread's input blocks,
    /// and to report how many it took. The old code compared that count and threw -- after a
    /// Ctrl-down had gone out, and before any Ctrl-up ever would. Windows then believes Ctrl is
    /// held by nobody, forever, and every subsequent keystroke on the machine arrives as a control
    /// chord. That is the second half of the field report: dots in Notepad, and then dots on
    /// everything typed afterwards.
    /// </para>
    /// <para>
    /// So the ups are sent from a <c>finally</c>, as their own call. Sending an up for a key that
    /// is already up is harmless; failing to send one is not.
    /// </para>
    /// </remarks>
    private void SendPaste()
    {
        // The gate has already confirmed Ctrl is up, so this press is unambiguous rather than
        // stacking on top of one the user is already holding.
        Span<InputRecord> chord =
        [
            InputRecord.VirtualKey(VirtualKeys.Control, keyUp: false),
            InputRecord.VirtualKey(VirtualKeys.KeyV, keyUp: false),
            InputRecord.VirtualKey(VirtualKeys.KeyV, keyUp: true),
            InputRecord.VirtualKey(VirtualKeys.Control, keyUp: true),
        ];

        var complete = false;
        try
        {
            var outcome = _sendInput.Send(chord);
            complete = outcome.Accepted == chord.Length;

            if (!complete)
            {
                throw new InvalidOperationException(outcome.LastError == SendInputOutcome.ErrorAccessDenied
                    ? "Windows refused the paste keystroke (UIPI). The focused window runs at a higher integrity level than Jane."
                    : $"SendInput accepted {outcome.Accepted} of {chord.Length} records (error {outcome.LastError}).");
            }
        }
        finally
        {
            if (!complete)
            {
                ReleasePasteChord();
            }
        }
    }

    /// <summary>Sends the two key-ups on their own, ignoring whether they land.</summary>
    private void ReleasePasteChord()
    {
        Span<InputRecord> release =
        [
            InputRecord.VirtualKey(VirtualKeys.KeyV, keyUp: true),
            InputRecord.VirtualKey(VirtualKeys.Control, keyUp: true),
        ];

        try
        {
            _sendInput.Send(release);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Runs inside a finally on a path that is already failing. The post-injection sweep in
            // InjectAsync is the remaining backstop, and it reads the real key state rather than
            // guessing from what was sent.
        }
    }

    /// <summary>Returns whether the user got their clipboard back, for the log rather than for control flow.</summary>
    private bool Restore(List<ClipboardPayload> saved)
    {
        try
        {
            _clipboard.SetContents(saved);
            return true;
        }
        catch (Exception)
        {
            // The restore is the last thing that runs and there is nothing left to fall back
            // to. Letting it throw here would replace a useful injection failure with a
            // clipboard one, and would do so from inside a finally block. It is recorded, though:
            // a user whose clipboard silently emptied deserves a line naming the dictation.
            return false;
        }
    }

    private static InjectionResult Failed(
        long startedAt, InjectionFailure failure, string detail, InjectionDiagnostics diagnostics) =>
        new(false, InjectionStrategy.Clipboard, 0, Stopwatch.GetElapsedTime(startedAt), failure, detail)
        { Diagnostics = diagnostics };
}
