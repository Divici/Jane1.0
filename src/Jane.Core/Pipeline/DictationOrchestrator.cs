using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Core.History;
using Jane.Core.Modes;

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

    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private readonly TaskCompletionSource _engineReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PipelineStatus _status = PipelineStatus.Idle;
    private TargetWindow _target = TargetWindow.None;
    private CapturedAudio? _pending;
    private Task _inFlight = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

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
        UndoStack? undo = null)
    {
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
            try
            {
                // Deep Context terms feed contextual biasing, which Phase 1's bench measured at 88%
                // of greedy -- affordable, so it is on.
                var options = context.Hotwords.Count > 0
                    ? new RecognitionOptions(context.Hotwords)
                    : RecognitionOptions.Default;

                recognition = await _recognizer.TranscribeAsync(voice.Trimmed, options, cancellationToken);
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
                return;
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

            Transition(new PipelineStatus(PipelineState.Injecting));
            var injection = await _injector.InjectAsync(text, _target, cancellationToken);

            if (!injection.Succeeded)
            {
                Fail(PipelineFailure.InjectionAborted,
                    injection.Detail ?? PipelineStatus.DefaultMessageFor(PipelineFailure.InjectionAborted));
                return;
            }

            // Only now. Submitting a form that never received the text is worse than not
            // submitting, so the Enter waits on a verified injection.
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
            // The buffer is dropped on every path, including the failing ones. Audio is never
            // retained beyond the dictation that produced it.
            _pending = null;
            Settle();
        }
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

    private static string DescribeDeviceFailure(Exception ex) =>
        ex.GetType().Name.Contains("Device", StringComparison.OrdinalIgnoreCase)
            ? ex.Message
            : PipelineStatus.DefaultMessageFor(PipelineFailure.NoMicrophone);

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
