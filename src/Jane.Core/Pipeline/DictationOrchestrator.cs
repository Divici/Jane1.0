using System.Diagnostics;
using System.Globalization;
using Jane.Core.Abstractions;
using Jane.Core.Diagnostics;
using Jane.Core.History;
using Jane.Core.Modes;
using Jane.Core.Text;

namespace Jane.Core.Pipeline;

/// <param name="EngineReadyTimeout">
/// How long a hotkey press will wait for the recogniser to finish loading before giving up.
/// Cold session-init measured 1.4 s in Phase 1's bench, so a press in the first couple of seconds
/// after launch is a real scenario, and losing that dictation would be worse than waiting for it.
/// </param>
/// <param name="ErrorDisplay">How long a failure stays on the pill before it returns to idle.</param>
public sealed record OrchestratorOptions
{
    public TimeSpan EngineReadyTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ErrorDisplay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether consecutive dictations into one window are separated by a space.
    /// </summary>
    /// <remarks>
    /// On, because injecting at the caret and adding nothing produced "Hello there.How are you?"
    /// for every sentence after the first. Off for anyone dictating into something where Jane's
    /// idea of a word boundary is wrong -- a code editor with its own completion, say.
    /// See <see cref="SpacingPolicy"/> for what "separated" means in the awkward cases.
    /// </remarks>
    public bool AutoSpace { get; init; } = true;

    /// <summary>
    /// The longest stretch of audio handed to the recogniser in one call.
    /// </summary>
    /// <remarks>
    /// Anything longer is split at pauses first. Offline sherpa-onnx models are trained and
    /// evaluated on utterances of a few seconds and degrade on multi-minute inputs, and a capture
    /// is capped at five minutes -- so the two ends of that range were a long way apart, with
    /// nothing in between. Thirty seconds matches the voice-activity detector's own per-segment
    /// ceiling, so the two agree about what "long" means.
    /// </remarks>
    public TimeSpan MaxRecognitionChunk { get; init; } = UtteranceChunker.DefaultMaxChunk;
}

/// <summary>
/// Drives one dictation from key-down to injected text.
/// </summary>
/// <remarks>
/// <para>
/// Everything that decides whether text reaches the user's window lives here, in
/// <c>Jane.Core</c>, deliberately free of Win32 -- which is what lets the whole set of routing and
/// failure rules be asserted with fakes rather than by holding a key down and hoping.
/// </para>
/// <para>
/// One dictation at a time. A second key-down while a pipeline is in flight is ignored rather
/// than queued: two dictations racing to inject into the same window is a worse outcome than a
/// dropped press, and the user can simply press again.
/// </para>
/// </remarks>
public sealed class DictationOrchestrator : IAsyncDisposable
{
    private readonly IAudioSource _audio;
    private readonly ISpeechRecognizer _recognizer;
    private readonly IVoiceActivityGate _vad;
    private readonly ITranscriptFormatter _formatter;
    private readonly ITextInjector _injector;
    private readonly IFocusTracker _focus;
    private readonly IDictationContextSource _context;
    private readonly ModeSelector _modes;
    private readonly EditModeHandler _edit;
    private readonly ISelectionRewriter _rewriter;
    private readonly ISubmitter? _submitter;
    private readonly OrchestratorOptions _options;
    private readonly IJaneLog _log;

    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private readonly TaskCompletionSource _engineReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PipelineStatus _status = PipelineStatus.Idle;
    private TargetWindow _target = TargetWindow.None;
    private CapturedAudio? _pending;
    private Task _inFlight = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    /// <summary>
    /// What Jane last put into <see cref="_spacedAgainst"/>, so the next dictation can be spaced
    /// off it. Set only on a verified injection: text that never arrived is nothing to space from.
    /// </summary>
    private string? _lastInjected;
    private TargetWindow _spacedAgainst = TargetWindow.None;

    /// <summary>
    /// Whether consecutive dictations into one window are separated by a space.
    /// </summary>
    /// <remarks>
    /// Seeded from <see cref="OrchestratorOptions.AutoSpace"/> and settable afterwards, because
    /// the settings window has to be able to change it on a running Jane. Everything else in the
    /// options is a startup timeout, where live change would mean nothing.
    /// </remarks>
    public bool AutoSpace { get; set; }

    /// <summary>
    /// What sits before the caret, as far as Jane can honestly claim to know.
    /// </summary>
    /// <remarks>
    /// Only its own last injection into this very window counts. UIA could in principle be asked,
    /// but it returns the enclosing paragraph without a caret offset, costs round trips inside an
    /// 80ms budget, and is refused outright by a good share of the applications people dictate
    /// into. Returning null means "unknown", and unknown means add nothing.
    /// </remarks>
    private string? PrecedingText() =>
        _lastInjected is not null && !_spacedAgainst.IsNone && _target.MatchesIdentity(_spacedAgainst)
            ? _lastInjected
            : null;

    public DictationOrchestrator(
        IAudioSource audio,
        ISpeechRecognizer recognizer,
        IVoiceActivityGate vad,
        ITranscriptFormatter formatter,
        ITextInjector injector,
        IFocusTracker focus,
        OrchestratorOptions? options = null,
        IDictationContextSource? context = null,
        ISelectionRewriter? rewriter = null,
        ISubmitter? submitter = null,
        ModeSelector? modes = null,
        UndoStack? undo = null,
        IJaneLog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _context = context ?? NullContextSource.Instance;
        _modes = modes ?? new ModeSelector();
        _edit = new EditModeHandler(undo ?? new UndoStack());

        // Without a rewriter, Edit Mode can still delete, replace and undo -- all of which are
        // local operations. Only "make it shorter" needs a model, and it says so rather than
        // silently doing nothing.
        _rewriter = rewriter ?? UnavailableRewriter.Instance;
        _submitter = submitter;
        _audio = audio;
        _recognizer = recognizer;
        _vad = vad;
        _formatter = formatter;
        _injector = injector;
        _focus = focus;
        _options = options ?? new OrchestratorOptions();
        AutoSpace = _options.AutoSpace;
    }

    public PipelineStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Raised on every transition. The overlay subscribes to exactly this and nothing else.
    /// </summary>
    /// <remarks>
    /// Fires on whichever thread caused the transition, which is not the UI thread. The overlay's
    /// presenter marshals to the dispatcher itself rather than making every caller here do it.
    /// </remarks>
    public event EventHandler<PipelineStatus>? StateChanged;

    /// <summary>Whether a dictation is currently running. Drives Esc consumption.</summary>
    public bool IsActive => Status.State is not (PipelineState.Idle or PipelineState.Cancelled or PipelineState.Failed);

    /// <summary>Test seam: the buffer must be null after every terminal state.</summary>
    internal CapturedAudio? DebugPendingAudio => _pending;

    /// <summary>
    /// Opens the microphone and loads the recogniser, both before the first hotkey press.
    /// </summary>
    /// <remarks>
    /// This is where the plan's latency budget is actually earned. Cold ASR session-init measured
    /// 1.4 s and device open ~60 ms; paying either on key-down would put them on the path the user
    /// feels most. Both are paid here instead, while the tray icon is appearing.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_engineReady.Task.IsCompleted)
        {
            return;
        }

        try
        {
            await _audio.OpenAsync(cancellationToken);
            await _recognizer.LoadAsync(cancellationToken);
            _engineReady.TrySetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed load is not fatal to the process: the tray icon still works, settings still
            // opens, and the user can download the model from there. Every later press reports it.
            _engineReady.TrySetException(new SpeechEngineUnavailableException(
                "The speech engine could not be loaded.", ex));
        }
    }

    /// <summary>
    /// Consumes one hotkey event. Returns immediately; the pipeline runs in the background.
    /// </summary>
    /// <remarks>
    /// Called from the hook's pump thread, which must not be blocked -- Windows removes a
    /// low-level hook whose callback overruns, and the hotkey then stops working with no error
    /// anywhere.
    /// </remarks>
    public void OnHotkey(HotkeyEvent hotkeyEvent)
    {
        if (_disposed)
        {
            return;
        }

        switch (hotkeyEvent.Kind)
        {
            case HotkeyEventKind.Pressed:
                BeginArming();
                break;

            case HotkeyEventKind.Released:
                Release(CaptureStopReason.Released);
                break;

            case HotkeyEventKind.TooShort:
                // Silently. Not even an error: on a common game bind the press usually was not
                // meant for Jane at all.
                Abandon(CaptureStopReason.TooShort, PipelineState.Idle);
                break;

            case HotkeyEventKind.Cancelled:
                Abandon(CaptureStopReason.Cancelled, PipelineState.Cancelled);
                break;

            default:
                break;
        }
    }

    private void BeginArming()
    {
        // Ignore rather than queue: see the class remarks.
        if (!_pipelineGate.Wait(0))
        {
            return;
        }

        try
        {
            _cancellation = new CancellationTokenSource();

            // Captured at key-down, and re-verified immediately before injection. Reading it at
            // injection time instead would send the text wherever focus happened to land while
            // the user was speaking.
            _target = _focus.GetForegroundWindow();

            // Started at key-down so the cross-process round trips overlap with the user speaking
            // rather than being paid after they stop.
            _context.BeginRead(_target);

            _audio.Arm();
            Transition(new PipelineStatus(PipelineState.Arming));
        }
        catch (Exception ex)
        {
            _pipelineGate.Release();

            // Logged as well as shown. Every dictation that failed this way left no line at all,
            // so a log covering two days of a broken microphone recorded one success per launch
            // and nothing else -- and the exception that named the cause was never written down.
            _log.Write(LogLevel.Warning, "dictation", DescribeDeviceFailure(ex), LogFields.New()
                .Add("result", PipelineFailure.NoMicrophone)
                .Add("app", _target.ProcessName)
                .Add("exception", ex.GetType().FullName)
                .Add("detail", ex.Message)
                .Add("inner", ex.InnerException?.Message));

            Fail(PipelineFailure.NoMicrophone, DescribeDeviceFailure(ex));
        }
    }

    private void Release(CaptureStopReason reason)
    {
        if (Status.State is not (PipelineState.Arming or PipelineState.Listening))
        {
            return;
        }

        var audio = _audio.Stop(reason);
        _pending = audio;
        _inFlight = RunAsync(audio, _cancellation?.Token ?? CancellationToken.None);
    }

    private void Abandon(CaptureStopReason reason, PipelineState terminal)
    {
        if (Status.State is not (PipelineState.Arming or PipelineState.Listening))
        {
            // Esc during Transcribing cancels the work in flight; the pipeline task settles it.
            _cancellation?.Cancel();
            return;
        }

        _ = _audio.Stop(reason);
        _pending = null;
        Transition(new PipelineStatus(terminal));
        Settle();
    }

    private async Task RunAsync(CapturedAudio audio, CancellationToken cancellationToken)
    {
        // Captured before the try so the log line can be written from the finally on every path,
        // including the ones that throw. The target is copied because a second key-down during
        // the tail of this dictation would otherwise rewrite the field before the line is built.
        var startedAt = Stopwatch.GetTimestamp();
        var target = _target;
        var recognitionTime = TimeSpan.Zero;
        InjectionResult? injection = null;

        try
        {
            Transition(new PipelineStatus(PipelineState.Listening));

            var voice = _vad.Process(audio.Samples);
            if (!voice.ContainsSpeech)
            {
                Fail(PipelineFailure.NoSpeech, PipelineStatus.DefaultMessageFor(PipelineFailure.NoSpeech));
                return;
            }

            // Wait for the engine only once we know there is speech, so a stray press never
            // blocks on a load it was not going to use.
            if (!await WaitForEngineAsync(cancellationToken))
            {
                return;
            }

            // Collected here rather than at key-down: the read was *started* then, concurrent with
            // speech, so by now it has usually finished and costs nothing. Only a wedged provider
            // pays the deadline.
            var context = await _context.CollectAsync(_target, cancellationToken);

            Transition(new PipelineStatus(PipelineState.Transcribing));
            RecognitionResult recognition;
            var recognitionStartedAt = Stopwatch.GetTimestamp();
            try
            {
                // Deep Context terms feed contextual biasing, which Phase 1's bench measured at 88%
                // of greedy -- affordable, so it is on.
                var options = context.Hotwords.Count > 0
                    ? new RecognitionOptions(context.Hotwords)
                    : RecognitionOptions.Default;

                recognition = await TranscribeAsync(voice, options, cancellationToken);
            }
            catch (SpeechEngineUnavailableException ex)
            {
                Fail(PipelineFailure.EngineUnavailable, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The user sees a designed message; the exception type belongs in the log.
                Fail(PipelineFailure.RecognitionFailed, PipelineStatus.DefaultMessageFor(PipelineFailure.RecognitionFailed));
                Debug.WriteLine($"Recognition failed: {ex}");
                _log.Write(LogLevel.Error, "asr", "Recognition threw.", LogFields.New()
                    .Add("engine", _recognizer.EngineId)
                    .Add("exception", ex.GetType().Name)
                    .Add("detail", ex.Message));
                return;
            }
            finally
            {
                recognitionTime = Stopwatch.GetElapsedTime(recognitionStartedAt);
            }

            if (recognition.IsEmpty)
            {
                // Skips both the LLM and injection: there is nothing to format and nothing to type.
                Fail(PipelineFailure.NoSpeech, PipelineStatus.DefaultMessageFor(PipelineFailure.NoSpeech));
                return;
            }

            // Always through Formatting, even when the formatter is a pass-through. The state
            // machine is the plan's, and the overlay collapses Transcribing and Formatting into
            // one visual anyway, so branching here would buy nothing and would make the in-game
            // route a different code path from the ordinary one.
            Transition(new PipelineStatus(PipelineState.Formatting));

            var mode = _modes.Decide(_target, context.Selection, context.ControlType);
            var formattingContext = new FormattingContext(
                _target.ProcessName, context.Hotwords, context.ScreenContext);

            if (mode.Mode == DictationModeKind.EditUnavailable)
            {
                // The whole point of BLOCKER #8. Something is selected, Jane cannot read it, and
                // the alternative to saying so is typing "make it shorter" into the document.
                Transition(new PipelineStatus(PipelineState.Failed, PipelineFailure.EditModeUnavailable, mode.Reason));
                return;
            }

            var submit = false;
            string text;

            if (mode.Mode == DictationModeKind.Edit)
            {
                var command = EditCommandParser.Parse(recognition.Text);
                var outcome = await _edit.ApplyAsync(
                    command, mode.Selection,
                    (selection, instruction, token) =>
                        _rewriter.RewriteAsync(selection, instruction, formattingContext, token),
                    cancellationToken);

                if (!outcome.ShouldInject)
                {
                    Transition(new PipelineStatus(PipelineState.Failed, PipelineFailure.None, outcome.Message));
                    return;
                }

                text = outcome.Text;
            }
            else
            {
                // "Send it" is stripped before formatting, so the phrase never reaches the LLM and
                // cannot be turned into prose.
                (var spoken, submit) = EditCommandParser.StripSendIt(recognition.Text);

                text = string.IsNullOrWhiteSpace(spoken)
                    ? string.Empty
                    : await _formatter.FormatAsync(spoken, formattingContext, cancellationToken);
            }

            // A delete is legitimately empty; a formatted dictation that came back empty is not.
            if (string.IsNullOrWhiteSpace(text) && mode.Mode != DictationModeKind.Edit)
            {
                Fail(PipelineFailure.NoSpeech, PipelineStatus.DefaultMessageFor(PipelineFailure.NoSpeech));
                return;
            }

            // Edit mode replaces a selection rather than appending at a caret, so a separator
            // there would land inside the rewritten span.
            if (AutoSpace && mode.Mode != DictationModeKind.Edit)
            {
                text = SpacingPolicy.Apply(text, PrecedingText());
            }

            Transition(new PipelineStatus(PipelineState.Injecting));
            injection = await _injector.InjectAsync(text, _target, cancellationToken);

            if (!injection.Succeeded)
            {
                // Deliberately not remembered: nothing reached the window, so the next dictation
                // has nothing to be spaced from.
                Fail(PipelineFailure.InjectionAborted,
                    injection.Detail ?? PipelineStatus.DefaultMessageFor(PipelineFailure.InjectionAborted));
                return;
            }

            // Only now. Submitting a form that never received the text is worse than not
            // submitting, so the Enter waits on a verified injection.
            // A submitted dictation is gone from the box it was typed into, so the next one has
            // nothing on screen to be spaced from.
            _lastInjected = submit ? null : text;
            _spacedAgainst = submit ? TargetWindow.None : _target;

            if (submit && _submitter is not null)
            {
                await _submitter.SubmitAsync(_target, cancellationToken);
            }

            Transition(PipelineStatus.Idle);
        }
        catch (OperationCanceledException)
        {
            Transition(new PipelineStatus(PipelineState.Cancelled));
        }
        finally
        {
            LogDictation(target, audio, recognitionTime, injection, Stopwatch.GetElapsedTime(startedAt));

            // The buffer is dropped on every path, including the failing ones. Audio is never
            // retained beyond the dictation that produced it.
            _pending = null;
            Settle();
        }
    }

    /// <summary>
    /// Writes the one line that says what this dictation did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transcript is deliberately absent. A log is a file people mail to each other, the text
    /// is already in a history table the user can inspect and clear, and a character count answers
    /// every question an injection bug raises. What goes in instead is the mechanism: which
    /// strategy ran, what the modifier gate saw, how many records Windows accepted, how long the
    /// paste took to settle.
    /// </para>
    /// <para>
    /// Written on every terminal path. A dictation that produced nothing is the case most in need
    /// of an explanation, and the one with no history row to look at afterwards.
    /// </para>
    /// </remarks>
    private void LogDictation(
        TargetWindow target,
        CapturedAudio audio,
        TimeSpan recognitionTime,
        InjectionResult? injection,
        TimeSpan total)
    {
        var status = Status;
        var diagnostics = injection?.Diagnostics ?? InjectionDiagnostics.Empty;
        var failedInjection = injection is { Succeeded: false };

        // An injection failure names itself rather than deferring to the pipeline's generic
        // "InjectionAborted", because which of the six ways it failed is the whole question.
        var result = failedInjection ? injection!.Failure.ToString()
            : status.State == PipelineState.Failed ? status.Failure.ToString()
            : status.State == PipelineState.Cancelled ? "Cancelled"
            : "ok";

        var message = status.State == PipelineState.Failed
            ? status.Message ?? PipelineStatus.DefaultMessageFor(status.Failure)
            : status.State == PipelineState.Cancelled ? "Cancelled before it finished."
            : "Injected.";

        _log.Write(
            status.State == PipelineState.Failed ? LogLevel.Warning : LogLevel.Info,
            "dictation",
            message,
            LogFields.New()
                .Add("app", target.ProcessName)
                .Add("class", target.WindowClass)
                .Add("strategy", injection?.Strategy)
                .Add("result", result)
                .Add("chars", injection?.CharactersSent)
                .Add("audio", audio.Duration)
                .Add("preroll", AudioFormat.DurationOf(audio.PreRollSamples))
                .Add("stop", audio.StopReason)
                .Add("asr", recognitionTime)
                .Add("inject", injection?.Elapsed)
                .Add("total", total)
                .Add("records", Records(diagnostics))
                .Add("mods-at-start", diagnostics.ModifiersInitiallyHeld)
                .Add("mods-held", diagnostics.ModifiersStillHeld)
                .Add("mods-stuck", diagnostics.ModifiersStuckAfter)
                .Add("mods-wait", diagnostics.ModifierWait)
                .Add("settle", diagnostics.PasteSettle)
                .Add("settle-why", diagnostics.PasteSettleReason)
                .Add("clip-restored", diagnostics.ClipboardRestored));
    }

    private static string? Records(InjectionDiagnostics diagnostics) =>
        diagnostics.RecordsSent == 0
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{diagnostics.RecordsAccepted}/{diagnostics.RecordsSent}");

    /// <summary>
    /// Recognises the utterance, in one call or several, cutting only at pauses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictation short enough to fit the budget takes exactly the path it always took: one call,
    /// one result, no joining. That is the overwhelmingly common case and it must not get slower
    /// or subtly different to serve the rare one.
    /// </para>
    /// <para>
    /// A longer one is split at silences the voice-activity gate already found -- see
    /// <see cref="UtteranceChunker"/> for why never cutting inside speech is the governing rule --
    /// and the pieces are joined the same way two consecutive dictations into one window are, so
    /// a chunk boundary reads like the sentence boundary it actually is.
    /// </para>
    /// <para>
    /// Word timings come back rebased onto the whole utterance rather than onto each chunk. A
    /// consumer given per-chunk offsets would silently place every word after the first boundary
    /// in the wrong place.
    /// </para>
    /// </remarks>
    private async Task<RecognitionResult> TranscribeAsync(
        VoiceActivityResult voice, RecognitionOptions options, CancellationToken cancellationToken)
    {
        var chunks = UtteranceChunker.Chunk(
            voice.Segments ?? [],
            voice.Trimmed.Length,
            AudioFormat.SamplesFor(_options.MaxRecognitionChunk));

        if (chunks.Count == 1)
        {
            return await _recognizer.TranscribeAsync(voice.Trimmed, options, cancellationToken);
        }

        List<RecognitionResult> pieces = new(chunks.Count);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slice = voice.Trimmed.Slice(chunk.Start, Math.Min(chunk.Length, voice.Trimmed.Length - chunk.Start));
            pieces.Add(await _recognizer.TranscribeAsync(slice, options, cancellationToken));
        }

        _log.Write(LogLevel.Info, "asr", "Long utterance recognised in pieces.", LogFields.New()
            .Add("chunks", chunks.Count)
            .Add("audio", AudioFormat.DurationOf(voice.Trimmed.Length))
            .Add("segments", voice.Segments?.Count));

        return UtteranceChunker.Join(pieces, chunks);
    }

    private async Task<bool> WaitForEngineAsync(CancellationToken cancellationToken)
    {
        if (_engineReady.Task.IsCompletedSuccessfully)
        {
            return true;
        }

        try
        {
            await _engineReady.Task.WaitAsync(_options.EngineReadyTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            Fail(PipelineFailure.EngineStillLoading, PipelineStatus.DefaultMessageFor(PipelineFailure.EngineStillLoading));
            return false;
        }
        catch (SpeechEngineUnavailableException ex)
        {
            Fail(PipelineFailure.EngineUnavailable, ex.Message);
            return false;
        }
    }

    private void Fail(PipelineFailure failure, string message) =>
        Transition(new PipelineStatus(PipelineState.Failed, failure, message));

    private void Transition(PipelineStatus status)
    {
        Volatile.Write(ref _status, status);
        StateChanged?.Invoke(this, status);
    }

    /// <summary>Releases the pipeline gate exactly once per dictation.</summary>
    private void Settle()
    {
        if (_pipelineGate.CurrentCount == 0)
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _pipelineGate.Release();
        }
    }

    /// <summary>
    /// Says what actually went wrong with the microphone, in the words of whatever reported it.
    /// </summary>
    /// <remarks>
    /// This used to substitute a generic "No microphone. Check Settings > System > Sound > Input."
    /// for any exception whose type name did not happen to contain "Device". The exception that
    /// broke every dictation after the first said "The audio client is already initialized" --
    /// one line that would have named the bug -- and it was replaced with advice to go and look
    /// at a sound setting that was perfectly fine. A message the user cannot act on is bad; one
    /// that sends them somewhere irrelevant is worse.
    /// </remarks>
    private static string DescribeDeviceFailure(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message)
            ? PipelineStatus.DefaultMessageFor(PipelineFailure.NoMicrophone)
            : ex.Message;

    /// <summary>The undo stack, so a focus change can clear it.</summary>
    public UndoStack UndoStack => _edit.Undo;

    /// <summary>Waits for the current dictation to reach a terminal state. Test and shutdown seam.</summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        await _inFlight.WaitAsync(cancellationToken);

        // A terminal state is reached inside RunAsync's finally, so the task completing is enough;
        // this just makes the wait explicit for callers that then read Status.
        await Task.Yield();
    }

    /// <summary>Waits until the pipeline reports the given state. Test seam.</summary>
    public async Task WaitForStateAsync(PipelineState state, CancellationToken cancellationToken)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, PipelineStatus status)
        {
            if (status.State == state)
            {
                reached.TrySetResult();
            }
        }

        StateChanged += Handler;
        try
        {
            if (Status.State == state)
            {
                return;
            }

            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            StateChanged -= Handler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();

        try
        {
            await _inFlight.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Shutting down anyway.
        }

        _cancellation?.Dispose();
        _recognizer.Dispose();
        await _audio.DisposeAsync();
        _pipelineGate.Dispose();
    }
}

/// <summary>
/// The Phase 5 formatter: returns the transcript unchanged.
/// </summary>
/// <remarks>
/// Parakeet already emits punctuation and casing, so raw output is presentable text. This is also
/// exactly what the in-game route injects once Phase 6 lands, which is why it is a real
/// implementation of the interface rather than a null check inside the orchestrator.
/// </remarks>
public sealed class PassthroughFormatter : ITranscriptFormatter
{
    public Task<string> FormatAsync(string transcript, FormattingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(transcript);

}
