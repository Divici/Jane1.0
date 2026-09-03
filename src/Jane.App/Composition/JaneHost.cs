using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Jane.App.Onboarding;
using Jane.App.Overlay;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Formatting;
using Jane.Core.History;
using Jane.Core.Instructions;
using Jane.Core.Pipeline;
using Jane.Core.Platform;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Core.Vocabulary;
using Jane.Llm;
using Jane.Speech;
using Jane.Windows.Audio;
using Jane.Windows.Automation;
using Jane.Windows.Gpu;
using Jane.Windows.Hotkeys;
using Jane.Windows.Injection;

namespace Jane.App.Composition;

/// <summary>
/// Builds the live dictation graph and holds it for the app's lifetime.
/// </summary>
/// <remarks>
/// The composition root. Every seam the phases introduced is bound to its real implementation
/// exactly once here, so the wiring is readable in one place rather than scattered through the
/// classes that use it.
/// </remarks>
public sealed class JaneHost : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly WasapiCapture _capture;
    private readonly ISpeechRecognizer _recognizer;
    private readonly SileroVadGate _vad;
    private readonly LowLevelKeyboardHook _hotkeys;
    private readonly IOverlayPresenter _overlay;
    private readonly LlmStack? _llm;
    private readonly JaneDatabase _database;
    private readonly UiaContextReader _context;
    private readonly IFocusTracker _focus;
    private readonly ITextInjector _injector;
    private readonly OverlayLevelPump _levelPump;
    private readonly LiveSettings _live;

    /// <summary>
    /// Watches for a game or a video taking the screen, so the resting pill can get out of the way.
    /// </summary>
    /// <remarks>
    /// Four seconds, and only while the resting pill is enabled. It is two cheap syscalls -- the
    /// same pair <see cref="FullscreenDetector"/> makes for the GPU governor -- and a game does not
    /// start and stop within one of them, so anything faster is polling for its own sake.
    /// </remarks>
    private static readonly TimeSpan FullscreenPollInterval = TimeSpan.FromSeconds(4);

    private readonly FullscreenDetector _fullscreen = new();
    private readonly Timer _fullscreenPoll;

    private OverlaySettings _overlaySettings = new();
    private HotkeyBinding _binding = HotkeyBinding.Default;
    private bool _screenIsBusy;
    private bool _paused;
    private bool _disposed;

    private JaneHost(
        Dispatcher dispatcher,
        SettingsStore settings,
        WasapiCapture capture,
        ISpeechRecognizer recognizer,
        SileroVadGate vad,
        LowLevelKeyboardHook hotkeys,
        DictationOrchestrator orchestrator,
        IOverlayPresenter overlay,
        LlmStack? llm,
        JaneDatabase database,
        UserDictionary dictionary,
        CustomInstructions instructions,
        HistoryStore history,
        UiaContextReader context,
        IFocusTracker focus,
        ITextInjector injector,
        SettingsRepository settingsRepository,
        IModelProvisioner provisioner)
    {
        _context = context;
        _focus = focus;
        _injector = injector;
        Settings2 = settingsRepository;
        Provisioner = provisioner;
        _llm = llm;
        _database = database;
        Dictionary = dictionary;
        Instructions = instructions;
        History = history;
        _dispatcher = dispatcher;
        _settings = settings;
        _capture = capture;
        _recognizer = recognizer;
        _vad = vad;
        _hotkeys = hotkeys;
        _overlay = overlay;
        Orchestrator = orchestrator;

        // The waveform's level: 30 fps of it, for as long as a dictation is listening. Without
        // this the pill's bars are drawn once, at the silence that precedes the first word, and
        // stay flat for the whole dictation.
        _levelPump = new OverlayLevelPump(overlay, () => _capture.CurrentLevel);

        // One wire from the settings database to everything in this graph that holds a copy of a
        // setting. Anything not on it is a control that writes a row and changes nothing.
        _live = new LiveSettings(hotkeys, capture, OnOverlaySettingsChanged);

        _fullscreenPoll = new Timer(_ => PollFullscreen(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public DictationOrchestrator Orchestrator { get; }

    /// <summary>The user's vocabulary. Feeds both sherpa-onnx biasing and the LLM prompt.</summary>
    public UserDictionary Dictionary { get; }

    /// <summary>Global and per-app style rules. An app with rules never takes the bypass.</summary>
    public CustomInstructions Instructions { get; }

    /// <summary>Everything ever dictated, in plaintext, until the user deletes it.</summary>
    public HistoryStore History { get; }

    /// <summary>The database file itself, so the UI can name it when it says what Jane keeps.</summary>
    public JaneDatabase Database => _database;

    /// <summary>Settings in the database, which is what the settings window edits.</summary>
    public SettingsRepository Settings2 { get; }

    /// <summary>Downloads model weights and pulls LLM models, for settings and onboarding.</summary>
    public IModelProvisioner Provisioner { get; }

    public IMicrophoneCatalog Microphones { get; } = new WasapiMicrophoneCatalog();

    public IMicrophoneCheck MicrophoneCheck { get; } = new WasapiMicrophoneCheck();

    /// <summary>Deep Context's privacy gate, editable from settings.</summary>
    public Blocklist Blocklist => _context.Blocklist;

    public TargetWindow GetForegroundWindow() => _focus.GetForegroundWindow();

    /// <summary>
    /// Re-injects a history entry, through the same identity check a live dictation uses.
    /// </summary>
    /// <remarks>
    /// A recorded window can be gone, or its handle recycled by a different process. Checking
    /// before typing is what stops a re-inject putting an old dictation into whatever now occupies
    /// that handle.
    /// </remarks>
    public async Task<bool> ReinjectAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var live = _focus.GetForegroundWindow();
        if (entry.CheckReinject(live) != ReinjectCheck.Ready)
        {
            return false;
        }

        var result = await _injector.InjectAsync(entry.FinalText, live, cancellationToken);
        return result.Succeeded;
    }

    /// <summary>Runs one dictation into a scratch buffer, for onboarding's test dictation.</summary>
    public async Task<string?> TestDictationAsync(CancellationToken cancellationToken)
    {
        var captured = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(object? sender, PipelineStatus status)
        {
            if (status.State is PipelineState.Idle or PipelineState.Failed or PipelineState.Cancelled)
            {
                captured.TrySetResult(status.State == PipelineState.Idle ? LastInjectedText : null);
            }
        }

        Orchestrator.StateChanged += OnState;
        try
        {
            return await captured.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            Orchestrator.StateChanged -= OnState;
        }
    }

    /// <summary>The last text Jane injected, so onboarding can show what it heard.</summary>
    public string? LastInjectedText { get; private set; }

    /// <summary>
    /// Shows or hides the floating pill. Aqua's "Show Floating Bar".
    /// </summary>
    /// <remarks>
    /// Written through the settings database rather than held in a field, so it survives a
    /// restart and so the settings window and the tray cannot disagree about it. The change
    /// arrives back here through <see cref="LiveSettings"/> like any other.
    /// </remarks>
    public void SetOverlayVisible(bool visible) =>
        _ = Settings2.UpdateAsync(
            s => s with { Overlay = s.Overlay with { Visible = visible } },
            CancellationToken.None);

    private void OnOverlaySettingsChanged(OverlaySettings overlay)
    {
        _overlaySettings = overlay;
        _binding = Settings2.Current.Hotkey.ToBinding();

        if (_overlay is OverlayPresenter presenter)
        {
            _dispatcher.BeginInvoke(() => presenter.Window.Anchor = overlay.Anchor);
        }

        // Nothing to watch for when the pill never rests on screen anyway.
        var watching = overlay is { Visible: true, ShowWhenIdle: true };
        _fullscreenPoll.Change(
            watching ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
            watching ? FullscreenPollInterval : Timeout.InfiniteTimeSpan);

        if (!watching)
        {
            _screenIsBusy = false;
        }

        // Republish, so turning the resting pill on or off is visible immediately rather than
        // after whatever the user next dictates.
        RefreshOverlay(Orchestrator.Status);
    }

    private void OnCaptureStateChanged(object? sender, AudioSourceState state)
    {
        // Only while a dictation is waiting on the device. Every other capture state change --
        // the idle release letting go, a reconnect -- happens with nothing on screen that depends
        // on it, and republishing then would be work for no visible difference.
        if (Orchestrator.Status.State is PipelineState.Arming or PipelineState.Listening)
        {
            RefreshOverlay(Orchestrator.Status);
        }
    }

    private void PollFullscreen()
    {
        if (_disposed)
        {
            return;
        }

        var busy = _fullscreen.Detect().Signals != GameSignals.None;
        if (busy == _screenIsBusy)
        {
            return;
        }

        _screenIsBusy = busy;
        RefreshOverlay(Orchestrator.Status);
    }

    public JaneSettings Settings => _settings.Current;

    /// <summary>Reports startup progress so the tray tooltip can say "starting" rather than lying.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// The settings Jane actually starts from, given two stores that can disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 10 moved settings into SQLite and left <c>settings.json</c> in place, because `bench`
    /// still writes it and `bench` runs in another process while Jane is closed. The database
    /// imports that file once, when its settings table is empty, and never again -- re-importing
    /// on every start would let a stale file undo a change made in the settings window.
    /// </para>
    /// <para>
    /// Composition then read the file rather than the database, which meant every value the
    /// settings window wrote was ignored at startup. That is the reported hotkey bug: rebinding
    /// wrote a row nothing read, so Right Ctrl stayed bound across restarts as well as within one.
    /// </para>
    /// <para>
    /// The rule is: the database wins, because it is the only store a person edits. The single
    /// exception is a `bench` run newer than the one the database has recorded, which is a
    /// deliberate re-measurement of which engine this machine should use and is the one thing the
    /// file is still the authority on.
    /// </para>
    /// </remarks>
    public static JaneSettings ReadStartupSettings(SettingsStore benchFile, SettingsRepository database)
    {
        ArgumentNullException.ThrowIfNull(benchFile);
        ArgumentNullException.ThrowIfNull(database);

        var stored = database.Read();
        var bench = benchFile.Read();

        if (bench.BenchmarkedAt is not { } measured ||
            (stored.BenchmarkedAt is { } known && measured <= known))
        {
            return stored;
        }

        return stored with { Speech = bench.Speech, BenchmarkedAt = measured };
    }

    public static JaneHost Create(Dispatcher dispatcher, IOverlayPresenter overlay)
    {
        var paths = new JanePaths();
        var settings = new SettingsStore(paths);
        settings.StartWatching();

        // Settings, dictionary, instructions and history all live in one SQLite file. Opened
        // first, because it holds the settings everything below is composed from -- see
        // ReadStartupSettings for why the database rather than the file.
        var database = JaneDatabase.Open(paths);
        var settingsRepository = new SettingsRepository(database);
        var current = ReadStartupSettings(settings, settingsRepository);

        if (current.BenchmarkedAt != settingsRepository.Current.BenchmarkedAt)
        {
            // A newer bench result was adopted. Write it down, so the next start does not have to
            // work it out again and the settings window shows the engine actually in use.
            settingsRepository.WriteAsync(current, CancellationToken.None).GetAwaiter().GetResult();
        }

        // Engine selection comes from `bench`, not from a constant here. Re-running the bench
        // in another process is picked up on the next read; see SettingsStore.StartWatching.
        var recognizer = BuildRecognizer(paths, current.Speech);

        var vad = new SileroVadGate(ModelCatalog.SileroVad.ResolvePath(paths.Models));

        var capture = WasapiCapture.ForDefaultDevice(new AudioCaptureOptions()
            .With(current.Routing) with
        {
            MaxCaptureDuration = TimeSpan.FromMilliseconds(current.Hotkey.MaxToggleDurationMs),
        });

        var focus = new FocusedAppIdentity();
        var modifierGate = new ModifierGate(new Win32AsyncKeyState());
        var sendInput = new Win32SendInput();
        var clipboard = new Win32Clipboard();

        var injector = new RoutingTextInjector(
            new InjectionStrategySelector(),
            new SendInputInjector(focus, focus, sendInput, modifierGate),
            new ClipboardInjector(focus, focus, clipboard, sendInput, modifierGate));

        // The LLM stack is optional: if Ollama cannot be started, or the user has turned
        // formatting off, Jane still dictates and simply injects raw Parakeet output -- the same
        // path a running game takes.
        var llm = current.Llm.Enabled
            ? LlmStack.Create(Path.Combine(RepoOrInstallRoot(), "tools", "ollama", "ollama.exe"), current)
            : null;

        var dictionary = new UserDictionary(database);
        var instructions = new CustomInstructions(database);
        var history = new HistoryStore(database);

        // The formatter never learns what a GPU is. RoutedFormatter applies the governor's
        // decision, so "skip the LLM while gaming" is one place rather than a branch in every
        // formatter -- and the skipped dictations are still recorded, because Phase 12's
        // false-bypass rate is unmeasurable without them.
        ITranscriptFormatter formatter;
        if (llm is null)
        {
            formatter = new PassthroughFormatter();
        }
        else
        {
            var skipped = new SkippedDictationLog(history, focus.GetForegroundWindow);
            var inner = new TranscriptFormatter(
                llm.CreateClient(),
                new TranscriptFormatterOptions
                {
                    Model = current.Llm.GpuModel,
                    NumCtx = current.Llm.NumCtx,
                    KeepAlive = current.Llm.KeepAlive,
                },
                new StoredFormattingPolicySource(dictionary, instructions),
                new HistoryFormattingLog(history, focus.GetForegroundWindow));

            formatter = new RoutedFormatter(inner, () => llm.CurrentRoute, skipped.Record);
        }

        // Deep Context: one long-lived UIA worker, an 80 ms deadline, and the layered selection
        // probe that makes Edit Mode safe in Chromium.
        var uia = UiaContextReader.CreateDefault();
        var contextSource = new WindowsContextSource(uia, new SelectionProbe(clipboard, sendInput));

        var orchestrator = new DictationOrchestrator(
            capture,
            recognizer,
            new SileroVoiceActivityGate(vad),
            formatter,
            injector,
            focus,
            options: null,
            contextSource,
            llm is null
                ? UnavailableRewriter.Instance
                : new LlmSelectionRewriter(llm.CreateClient(), current.Llm.GpuModel, current.Llm.NumCtx),
            new SendInputSubmitter(sendInput));

        var hotkeys = new LowLevelKeyboardHook(
            current.Hotkey.ToBinding(),
            current.Hotkey.Mode,
            new HotkeyOptions
            {
                MinimumHold = TimeSpan.FromMilliseconds(current.Hotkey.MinimumHoldMs),
                MaxDuration = TimeSpan.FromMilliseconds(current.Hotkey.MaxToggleDurationMs),
            });

        var downloader = new ModelDownloader(new HttpClient(), paths.Models);

        // Onboarding must not leave a manual `ollama pull` as homework, so the provisioner drives
        // Jane's own supervised server rather than shelling out to the CLI.
        IModelProvisioner provisioner = llm is null
            ? new WeightsOnlyProvisioner(downloader)
            : new ModelProvisioner(
                downloader,
                (model, progress, token) => llm.Puller.PullAsync(
                    model,
                    progress is null ? null : new Progress<PullProgress>(p =>
                        progress.Report(new LlmPullProgress(model, p.Completed, p.Total, p.Status))),
                    token),
                (model, token) => llm.Puller.IsPresentAsync(model, token));

        return new JaneHost(
            dispatcher, settings, capture, recognizer, vad, hotkeys, orchestrator, overlay, llm,
            database, dictionary, instructions, history, uia, focus, injector,
            settingsRepository, provisioner);
    }

    /// <summary>
    /// Finds the directory holding <c>tools/ollama</c>: the repository root during development,
    /// the install directory once published.
    /// </summary>
    private static string RepoOrInstallRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "ollama")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static ISpeechRecognizer BuildRecognizer(JanePaths paths, SpeechSettings speech)
    {
        if (speech.EngineId.StartsWith("whisper", StringComparison.Ordinal))
        {
            var asset = ModelCatalog.WhisperQuants.FirstOrDefault(q => q.Id == speech.EngineId)
                        ?? ModelCatalog.WhisperQuants[0];
            return new WhisperNetRecognizer(new WhisperOptions(
                asset.ResolvePath(paths.Models), asset.Id, speech.NumThreads));
        }

        return new ParakeetRecognizer(new ParakeetOptions(
            ModelCatalog.ParakeetV2Int8.ResolvePath(paths.Models),
            speech.NumThreads,
            speech.EnableHotwordBiasing,
            speech.HotwordBoost));
    }

    /// <summary>
    /// Opens the microphone, loads the recogniser, and only then starts listening for the hotkey.
    /// </summary>
    /// <remarks>
    /// The order matters. Cold ASR session-init measured 1.4 s in Phase 1's bench; installing the
    /// hook first would mean the very first press lands on an engine that is not there yet. The
    /// orchestrator still waits rather than failing if someone is quick, but not arming until the
    /// engine is up keeps that path rare.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Orchestrator.StateChanged += OnPipelineStateChanged;

        // The device opening is its own event, not a pipeline transition: under the default
        // activation the pipeline reaches Arming while the driver is still handing over a stream.
        // Without this the pill would sit on "Connecting..." until something else happened to
        // republish it, which for a dictation in progress is not until the key comes up.
        _capture.StateChanged += OnCaptureStateChanged;

        // Applies the stored settings now and on every subsequent write, which is also what puts
        // the resting pill on screen for the first time.
        _live.Attach(Settings2);

        if (_llm is not null)
        {
            // Started before the hook, so the first dictation never races the server coming up.
            // A failure marks the GPU route degraded and Jane falls through to raw output.
            await _llm.StartAsync(cancellationToken);
        }

        await Orchestrator.StartAsync(cancellationToken);

        _hotkeys.HotkeyEvent += OnHotkeyEvent;
        _hotkeys.Start();

        // Esc is consumed only while a dictation is in flight; at every other moment it must
        // reach whatever app the user is actually in.
        Orchestrator.StateChanged += (_, status) => _hotkeys.NotifyPipelineActive(Orchestrator.IsActive);

        IsReady = true;

        // Now the resting pill can honestly invite a key press.
        RefreshOverlay(Orchestrator.Status);
    }

    /// <summary>Stops the hotkey firing without tearing the graph down.</summary>
    /// <remarks>
    /// The hook stays installed. The microphone needs no special handling either way: under the
    /// default activation it is only open while a dictation is running, and a paused Jane never
    /// starts one.
    /// </remarks>
    public void SetPaused(bool paused)
    {
        _paused = paused;

        // The resting pill says which of the two it is. Without this it keeps inviting a key
        // press that does nothing, and the user concludes Jane is broken rather than paused.
        RefreshOverlay(Orchestrator.Status);
    }

    public void SetMode(HotkeyMode mode) =>
        _ = Settings2.UpdateAsync(
            s => s with { Hotkey = s.Hotkey with { Mode = mode } },
            CancellationToken.None);

    private void OnHotkeyEvent(object? sender, HotkeyEvent hotkeyEvent)
    {
        if (_paused)
        {
            return;
        }

        // The governor is consulted at key-down, not at format time, so the warm-up overlaps with
        // the user speaking rather than starting once they have finished.
        if (hotkeyEvent.Kind == HotkeyEventKind.Pressed)
        {
            _llm?.BeginDictation(CancellationToken.None);
        }

        Orchestrator.OnHotkey(hotkeyEvent);
    }

    private void OnPipelineStateChanged(object? sender, PipelineStatus status)
    {
        RefreshOverlay(status);

        // Started after the status is published, stopped before the next one: the pump only ever
        // republishes a Listening status it found already in place.
        _levelPump.OnState(status);
    }

    /// <summary>
    /// Publishes what the pill should be showing right now.
    /// </summary>
    /// <remarks>
    /// The presenter marshals to the dispatcher itself, so this can be called from the pipeline's
    /// own thread without a hop here.
    /// </remarks>
    private void RefreshOverlay(PipelineStatus status)
    {
        var level = status.State is PipelineState.Arming or PipelineState.Listening
            ? _capture.CurrentLevel
            : 0f;

        _overlay.Show(IdleOverlay.For(
            status.ToOverlayStatus(level),
            _overlaySettings,
            _binding,
            _paused,
            fullscreen: _screenIsBusy,
            ready: IsReady,
            microphoneOpen: _capture.State.IsOpen));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _hotkeys.HotkeyEvent -= OnHotkeyEvent;
        _capture.StateChanged -= OnCaptureStateChanged;
        Orchestrator.StateChanged -= OnPipelineStateChanged;
        _live.Dispose();
        _levelPump.Dispose();
        await _fullscreenPoll.DisposeAsync();
        _hotkeys.Dispose();

        await Orchestrator.DisposeAsync();

        if (_llm is not null)
        {
            await _llm.DisposeAsync();
        }

        _context.Dispose();
        _vad.Dispose();
        _database.Dispose();
        _settings.Dispose();
    }
}

/// <summary>Adapts <see cref="SileroVadGate"/> to the orchestrator's Win32-free contract.</summary>
/// <remarks>
/// The adapter exists so <c>Jane.Core</c> never references sherpa-onnx: the orchestrator is
/// testable with a fake gate, and a missing VAD model still surfaces as the typed
/// <see cref="VadModelMissingException"/> from the real one rather than being quietly skipped.
/// </remarks>
public sealed class SileroVoiceActivityGate(SileroVadGate gate) : IVoiceActivityGate
{
    public VoiceActivityResult Process(ReadOnlyMemory<float> samples)
    {
        var result = gate.Process(samples);
        return new VoiceActivityResult(result.ContainsSpeech, result.Samples);
    }
}
