using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Windows.Automation;

namespace Jane.Windows.Injection;

/// <param name="MaxRecordsPerBatch">
/// The largest number of <c>INPUT</c> records handed to one <c>SendInput</c> call. Each
/// character costs two records (down and up), so the default of 80 is 40 characters per batch.
/// OpenWhispr#829 is the reason there is a ceiling at all: past roughly 200 characters an
/// oversized synthetic batch stops arriving, with no error and no text -- the worst possible
/// failure for a dictation tool, because the user has no way to tell it happened.
/// </param>
public sealed record SendInputInjectorOptions(int MaxRecordsPerBatch)
{
    public static SendInputInjectorOptions Default { get; } = new(MaxRecordsPerBatch: 80);
}

/// <summary>
/// Types the text, one UTF-16 code unit at a time, with <c>KEYEVENTF_UNICODE</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the strategy that never touches the user's clipboard, and the only one that works in
/// a console. <c>KEYEVENTF_UNICODE</c> hands Windows a code unit directly rather than a scan
/// code, so the active keyboard layout is irrelevant and accented Latin, CJK and emoji all
/// survive -- a character outside the BMP goes out as its two surrogate halves, in order, and
/// Windows reassembles them into the pair of WM_CHAR messages the target expects.
/// </para>
/// <para>
/// Line breaks are the one exception. U+000A delivered as a Unicode code unit is a bare line
/// feed that most Win32 edit controls ignore, so it is translated into a real VK_RETURN press;
/// CRLF and a lone CR collapse into the same single Enter.
/// </para>
/// </remarks>
public sealed class SendInputInjector(
    IFocusTracker focusTracker,
    IWindowLiveness liveness,
    ISendInput sendInput,
    ModifierGate modifierGate,
    SendInputInjectorOptions? options = null) : ITextInjector
{
    private readonly InjectionPreflight _preflight = new(focusTracker, liveness, modifierGate);
    private readonly SendInputInjectorOptions _options = options ?? SendInputInjectorOptions.Default;

    public async Task<InjectionResult> InjectAsync(
        string text, TargetWindow target, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        if (string.IsNullOrEmpty(text))
        {
            return new InjectionResult(true, InjectionStrategy.Unicode, 0, Stopwatch.GetElapsedTime(startedAt));
        }

        var preflight = await _preflight.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        var diagnostics = preflight.Describe();

        if (preflight.Abort is { } reason)
        {
            return new InjectionResult(
                false, InjectionStrategy.Unicode, 0, Stopwatch.GetElapsedTime(startedAt),
                reason.Failure, reason.Detail)
            { Diagnostics = diagnostics };
        }

        var normalised = NormaliseLineBreaks(text);
        var units = BuildUnits(normalised);
        var sent = 0;
        var accepted = 0;

        foreach (var batch in Batch(units, _options.MaxRecordsPerBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = sendInput.Send(batch);
            sent += batch.Length;
            accepted += (int)outcome.Accepted;

            if (outcome.Accepted == batch.Length)
            {
                continue;
            }

            // A refusal part-way through has already put some characters on screen. The result
            // reports zero sent regardless, because a partial injection is a failure the user
            // has to see and redo, not a partial success worth recording as delivered.
            var failure = outcome.LastError == SendInputOutcome.ErrorAccessDenied
                ? InjectionFailure.PrivilegeBlocked
                : InjectionFailure.Unknown;
            var detail = failure == InjectionFailure.PrivilegeBlocked
                ? "Windows refused the input (UIPI). The focused window runs at a higher integrity level than Jane; uiAccess signing is what lifts this."
                : $"SendInput accepted {outcome.Accepted} of {batch.Length} records (error {outcome.LastError}).";

            return new InjectionResult(
                false, InjectionStrategy.Unicode, 0, Stopwatch.GetElapsedTime(startedAt), failure, detail)
            { Diagnostics = diagnostics with { RecordsSent = sent, RecordsAccepted = accepted } };
        }

        return new InjectionResult(
            true, InjectionStrategy.Unicode, normalised.Length, Stopwatch.GetElapsedTime(startedAt))
        { Diagnostics = diagnostics with { RecordsSent = sent, RecordsAccepted = accepted } };
    }

    /// <summary>CRLF, lone CR and lone LF all mean one Enter press.</summary>
    private static string NormaliseLineBreaks(string text) =>
        text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;

    /// <summary>
    /// Groups the records into indivisible units -- one per character, but four for a surrogate
    /// pair -- so that batching can never leave half an astral character in one <c>SendInput</c>
    /// call and half in the next.
    /// </summary>
    private static List<InputRecord[]> BuildUnits(string text)
    {
        List<InputRecord[]> units = new(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\n')
            {
                units.Add([InputRecord.VirtualKey(VirtualKeys.Return, keyUp: false), InputRecord.VirtualKey(VirtualKeys.Return, keyUp: true)]);
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                units.Add(
                [
                    InputRecord.UnicodeCodeUnit(c, keyUp: false),
                    InputRecord.UnicodeCodeUnit(c, keyUp: true),
                    InputRecord.UnicodeCodeUnit(text[i + 1], keyUp: false),
                    InputRecord.UnicodeCodeUnit(text[i + 1], keyUp: true),
                ]);
                i++;
                continue;
            }

            units.Add([InputRecord.UnicodeCodeUnit(c, keyUp: false), InputRecord.UnicodeCodeUnit(c, keyUp: true)]);
        }

        return units;
    }

    private static IEnumerable<InputRecord[]> Batch(List<InputRecord[]> units, int maxRecords)
    {
        List<InputRecord> batch = new(maxRecords);

        foreach (var unit in units)
        {
            if (batch.Count > 0 && batch.Count + unit.Length > maxRecords)
            {
                yield return [.. batch];
                batch.Clear();
            }

            batch.AddRange(unit);
        }

        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }
}
