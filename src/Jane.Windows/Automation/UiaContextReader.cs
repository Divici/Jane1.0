using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Core.Vocabulary;

namespace Jane.Windows.Automation;

/// <param name="Deadline">
/// How long the pipeline is prepared to wait <em>at the point it asks</em>. The read itself starts
/// at key-down and runs while the user is speaking, so on a healthy provider this expires never;
/// it exists entirely for the one that has stopped answering.
/// </param>
/// <param name="Enabled">
/// The user's switch. Off means no read is started at all, not a read whose result is discarded.
/// </param>
public sealed record ContextReadOptions
{
    public static ContextReadOptions Default { get; } = new();

    public TimeSpan Deadline { get; init; } = TimeSpan.FromMilliseconds(80);

    public bool Enabled { get; init; } = true;

    public ContextExtractionOptions Extraction { get; init; } = ContextExtractionOptions.Default;

    public int MaxSelectionChars { get; init; } = 4_000;

    public int MaxSurroundingChars { get; init; } = 4_000;

    /// <summary>Passed to the session so it stops making round trips nobody will wait for.</summary>
    public TimeSpan SoftBudget { get; init; } = TimeSpan.FromMilliseconds(60);
}

/// <summary>
/// The result of one Deep Context read, as the pipeline and the settings screen see it.
/// </summary>
/// <param name="Caret">
/// Where the caret was, when the provider said. Feeds <see cref="CaretLocator"/> as the fallback
/// for applications that draw their own caret and create no system one.
/// </param>
/// <param name="RoundTrips">Cross-process calls the read cost. Zero when nothing was asked.</param>
/// <param name="Preceding">
/// The character before the caret, for automatic spacing. Null when unknown, empty at the very
/// start. Never set on a read that was refused or thrown away.
/// </param>
public sealed record ContextRead(
    ContextOutcome Outcome,
    ContextHints Hints,
    TargetWindow Target,
    string? Detail = null,
    CaretRect? Caret = null,
    TimeSpan Elapsed = default,
    int RoundTrips = 0,
    string? Preceding = null)
{
    public static ContextRead None { get; } =
        new(ContextOutcome.NotAttempted, ContextHints.Empty, TargetWindow.None);

    /// <summary>Whether the overlay should show its "context was used" indicator.</summary>
    public bool HasContext => !Hints.IsEmpty;
}

/// <summary>
/// A read that was started at key-down and can be collected later.
/// </summary>
/// <remarks>
/// Split from the read itself because the two happen at different moments in a dictation: the
/// request goes out on the key-down that opens the microphone, and the answer is needed at key-up,
/// when the hotword list is assembled for the recogniser. Between them the user is speaking, which
/// is the free time this design is built to spend.
/// </remarks>
public sealed class ContextReadHandle
{
    private readonly Task<ContextRead>? _pending;
    private readonly ContextRead? _immediate;
    private readonly UiaContextReader? _reader;
    private readonly TimeSpan _deadline;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal ContextReadHandle(ContextRead immediate)
    {
        _immediate = immediate;
        Target = immediate.Target;
    }

    internal ContextReadHandle(TargetWindow target, Task<ContextRead> pending, TimeSpan deadline, UiaContextReader reader)
    {
        Target = target;
        _pending = pending;
        _deadline = deadline;
        _reader = reader;
    }

    public TargetWindow Target { get; }

    public bool IsCompleted => _immediate is not null || _pending!.IsCompleted;

    /// <summary>
    /// Collects the read, waiting no longer than the deadline.
    /// </summary>
    /// <remarks>
    /// Blocking is correct here and not a smell: the caller is the pipeline step that assembles
    /// the recogniser's hotwords, it has nothing else to do, and the wait is bounded at 80 ms by
    /// construction. Giving up does not cancel anything -- there is nothing to cancel -- it simply
    /// stops waiting, and the worker's own wedge handling deals with what is left behind.
    /// </remarks>
    public ContextRead Wait(TimeSpan? deadline = null)
    {
        if (_immediate is { } immediate)
        {
            return immediate;
        }

        var budget = deadline ?? _deadline;
        if (_pending!.Wait(budget))
        {
            return _pending.GetAwaiter().GetResult();
        }

        return TimedOut();
    }

    public async Task<ContextRead> WaitAsync(TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        if (_immediate is { } immediate)
        {
            return immediate;
        }

        try
        {
            return await _pending!.WaitAsync(deadline ?? _deadline, cancellationToken);
        }
        catch (TimeoutException)
        {
            return TimedOut();
        }
    }

    private ContextRead TimedOut()
    {
        var read = new ContextRead(
            ContextOutcome.TimedOut,
            ContextHints.Empty,
            Target,
            "The UI Automation read did not answer inside the deadline.",
            Elapsed: _clock.Elapsed);

        _reader?.RecordTimeout(read);
        return read;
    }
}

/// <summary>
/// Deep Context, end to end: decide whether a window may be read, read it on the one worker
/// thread, and turn what comes back into a short list of terms.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline uses this in two steps. At key-down, <see cref="BeginRead"/> -- which never
/// blocks, never throws, and never touches the provider for a window the blocklist refuses. At
/// key-up, <c>handle.Wait()</c>, bounded by the deadline. The terms then go to two places:
/// <c>RecognitionOptions.Hotwords</c>, where sherpa-onnx contextual biasing actually changes the
/// transcription, and the LLM prompt.
/// </para>
/// <para>
/// The auto-blocklist is applied here rather than inside the worker because it is a policy
/// decision about a process, and the worker deliberately knows nothing about processes -- it knows
/// only that a call has not come back.
/// </para>
/// </remarks>
public sealed class UiaContextReader : IDisposable
{
    private readonly UiaWorker _worker;
    private readonly ContextReadOptions _options;
    private readonly bool _ownsWorker;

    private ContextRead _lastRead = ContextRead.None;
    private bool _disposed;

    /// <param name="ownsWorker">
    /// False by default. The worker outlives any one reader in the app -- it is created at startup
    /// with the rest of the graph -- so disposing it is the composition root's job, not this one's.
    /// </param>
    public UiaContextReader(
        UiaWorker worker,
        Blocklist blocklist,
        ContextReadOptions? options = null,
        bool ownsWorker = false)
    {
        _worker = worker;
        Blocklist = blocklist;
        _options = options ?? ContextReadOptions.Default;
        _ownsWorker = ownsWorker;
    }

    /// <summary>The shipping configuration: a real UI Automation worker and the default blocklist.</summary>
    public static UiaContextReader CreateDefault(
        Blocklist? blocklist = null,
        ContextReadOptions? options = null,
        UiaComOptions? com = null)
    {
        var readOptions = options ?? ContextReadOptions.Default;
        var worker = UiaWorker.CreateDefault(com, new UiaWorkerOptions { WedgeAfter = readOptions.Deadline });
        return new UiaContextReader(worker, blocklist ?? new Blocklist(), readOptions, ownsWorker: true);
    }

    public Blocklist Blocklist { get; }

    public UiaWorker Worker => _worker;

    /// <summary>
    /// Exactly what was captured for the last dictation, for the settings panel that promises to
    /// show it.
    /// </summary>
    public ContextRead LastRead => Volatile.Read(ref _lastRead);

    /// <summary>Raised on every completed read, including the refused ones. Feeds history.</summary>
    public event EventHandler<ContextRead>? ReadCompleted;

    /// <summary>
    /// Starts a read for the window that had focus at key-down. Returns immediately.
    /// </summary>
    public ContextReadHandle BeginRead(TargetWindow target)
    {
        if (_disposed || !_options.Enabled)
        {
            return Refuse(new ContextRead(ContextOutcome.Disabled, ContextHints.Empty, target));
        }

        if (target.IsNone)
        {
            return Refuse(new ContextRead(ContextOutcome.NotAttempted, ContextHints.Empty, target));
        }

        var decision = Blocklist.Evaluate(target.ProcessName, target.WindowTitle);
        if (decision.IsBlocked)
        {
            // The important part of this branch is what is missing from it: no UIA call is made,
            // so a blocked window is never touched even by a read whose result is thrown away.
            return Refuse(new ContextRead(
                ContextOutcome.Blocked, ContextHints.Empty, target, $"{decision.Reason}: {decision.Match}"));
        }

        if (_worker.IsWedged)
        {
            // Still stuck at the next key-down. That is the definition of wedged, and the process
            // it is stuck on is off for the session -- bounded, and it never happens twice.
            var stuck = _worker.InFlightTarget;
            Blocklist.AutoBlockForSession(
                stuck.ProcessName,
                $"the UI Automation read did not return after {_worker.InFlightFor.TotalMilliseconds:F0} ms");

            return Refuse(new ContextRead(
                ContextOutcome.WorkerWedged,
                ContextHints.Empty,
                target,
                stuck.IsNone ? "The UIA worker is wedged." : $"The UIA worker is wedged on {stuck.ProcessName}."));
        }

        if (_worker.State is UiaWorkerState.Unavailable or UiaWorkerState.Stopped)
        {
            return Refuse(new ContextRead(ContextOutcome.ProviderUnavailable, ContextHints.Empty, target));
        }

        var request = new UiaReadRequest(target)
        {
            MaxSelectionChars = _options.MaxSelectionChars,
            MaxSurroundingChars = _options.MaxSurroundingChars,
            SoftBudget = _options.SoftBudget,
        };

        if (!_worker.TrySubmit(request, out var completion))
        {
            // Busy, but not yet past the wedge threshold. Back-to-back dictation is legitimate, so
            // this costs the current press its context and nothing more.
            return Refuse(new ContextRead(
                ContextOutcome.WorkerBusy, ContextHints.Empty, target, "A previous read is still in flight."));
        }

        return new ContextReadHandle(target, MapAsync(completion, target), _options.Deadline, this);
    }

    /// <summary>Begin and collect in one call. Convenience for callers with nothing to overlap.</summary>
    public Task<ContextRead> ReadAsync(TargetWindow target, CancellationToken cancellationToken = default) =>
        BeginRead(target).WaitAsync(cancellationToken: cancellationToken);

    private async Task<ContextRead> MapAsync(Task<UiaRawRead> completion, TargetWindow target)
    {
        var raw = await completion;

        ContextRead read;
        try
        {
            read = Interpret(raw, target);
        }
        catch (Exception ex)
        {
            // This task is waited on with a deadline, and a faulted task throws out of that wait
            // rather than expiring. Interpretation is pure string work, so anything thrown here is
            // a bug -- but it must degrade to "no context", never to a failed dictation.
            Debug.WriteLine($"Deep Context interpretation failed: {ex}");
            read = new ContextRead(ContextOutcome.Empty, ContextHints.Empty, target, ex.Message);
        }

        Publish(read);
        return read;
    }

    private ContextRead Interpret(UiaRawRead raw, TargetWindow target)
    {
        var outcome = raw.Status switch
        {
            UiaReadStatus.PasswordControl => ContextOutcome.PasswordControl,
            UiaReadStatus.Unavailable => ContextOutcome.ProviderUnavailable,
            UiaReadStatus.FocusMoved => ContextOutcome.FocusMoved,
            UiaReadStatus.NoFocusedElement => ContextOutcome.Empty,
            UiaReadStatus.NoTextProvider => ContextOutcome.NoTextProvider,
            UiaReadStatus.ProviderError => ContextOutcome.Empty,
            _ => ContextOutcome.Read,
        };

        if (outcome is not ContextOutcome.Read)
        {
            return new ContextRead(outcome, ContextHints.Empty, target, raw.Detail, raw.Caret, raw.Elapsed, raw.RoundTrips);
        }

        // The second privacy layer. A page title says "Dashboard"; the URL says secure.chase.com.
        // Only the read can tell them apart, and a hit throws away all of it rather than guessing
        // which half was safe.
        var content = string.Join('\n', new[] { raw.Selection, raw.Surrounding, raw.Value }.Where(static part => part is not null));
        var contentDecision = Blocklist.EvaluateContent(content);
        if (contentDecision.IsBlocked)
        {
            return new ContextRead(
                ContextOutcome.BlockedContent,
                ContextHints.Empty,
                target,
                $"{contentDecision.Reason}: {contentDecision.Match}",
                raw.Caret,
                raw.Elapsed,
                raw.RoundTrips);
        }

        var hints = ContextTermExtractor.Extract(raw.Selection, raw.Surrounding, raw.Value, _options.Extraction);

        return new ContextRead(
            hints.IsEmpty ? ContextOutcome.Empty : ContextOutcome.Read,
            hints,
            target,
            raw.Detail,
            raw.Caret,
            raw.Elapsed,
            raw.RoundTrips,
            raw.Preceding);
    }

    private ContextReadHandle Refuse(ContextRead read)
    {
        Publish(read);
        return new ContextReadHandle(read);
    }

    internal void RecordTimeout(ContextRead read)
    {
        // Only if nothing better has landed. A read that finished a millisecond after the deadline
        // is still the truthful answer to "what did Jane capture", even though the pipeline had
        // already moved on without it.
        if (LastRead.Outcome is ContextOutcome.NotAttempted or ContextOutcome.TimedOut)
        {
            Publish(read);
        }
        else
        {
            ReadCompleted?.Invoke(this, read);
        }
    }

    private void Publish(ContextRead read)
    {
        Volatile.Write(ref _lastRead, read);
        ReadCompleted?.Invoke(this, read);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsWorker)
        {
            _worker.Dispose();
        }
    }
}
