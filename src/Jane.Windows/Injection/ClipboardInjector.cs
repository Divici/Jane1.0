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
public sealed record ClipboardInjectorOptions(TimeSpan PasteSettleDelay)
{
    public static ClipboardInjectorOptions Default { get; } = new(TimeSpan.FromMilliseconds(60));
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

        var abort = await _preflight.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        if (abort is { } reason)
        {
            return Failed(startedAt, reason.Failure, reason.Detail);
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
            return Failed(startedAt, InjectionFailure.ClipboardUnavailable, ex.Message);
        }

        try
        {
            _clipboard.SetContents(BuildPayload(text));
            SendPaste();
            await Task.Delay(_options.PasteSettleDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(startedAt, InjectionFailure.Unknown, ex.Message);
        }
        finally
        {
            Restore(saved);
        }

        return new InjectionResult(
            true, InjectionStrategy.Clipboard, text.Length, Stopwatch.GetElapsedTime(startedAt));
    }

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

        var outcome = _sendInput.Send(chord);
        if (outcome.Accepted != chord.Length)
        {
            throw new InvalidOperationException(outcome.LastError == SendInputOutcome.ErrorAccessDenied
                ? "Windows refused the paste keystroke (UIPI). The focused window runs at a higher integrity level than Jane."
                : $"SendInput accepted {outcome.Accepted} of {chord.Length} records (error {outcome.LastError}).");
        }
    }

    private void Restore(List<ClipboardPayload> saved)
    {
        try
        {
            _clipboard.SetContents(saved);
        }
        catch (Exception)
        {
            // The restore is the last thing that runs and there is nothing left to fall back
            // to. Letting it throw here would replace a useful injection failure with a
            // clipboard one, and would do so from inside a finally block.
        }
    }

    private static InjectionResult Failed(long startedAt, InjectionFailure failure, string detail) =>
        new(false, InjectionStrategy.Clipboard, 0, Stopwatch.GetElapsedTime(startedAt), failure, detail);
}
