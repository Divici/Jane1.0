using System.IO;
using System.Windows.Threading;
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
        HistoryStore history)
    {
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
    }

    public DictationOrchestrator Orchestrator { get; }

    /// <summary>The user's vocabulary. Feeds both sherpa-onnx biasing and the LLM prompt.</summary>
    public UserDictionary Dictionary { get; }

    /// <summary>Global and per-app style rules. An app with rules never takes the bypass.</summary>
    public CustomInstructions Instructions { get; }

    /// <summary>Everything ever dictated, in plaintext, until the user deletes it.</summary>
    public HistoryStore History { get; }

    public JaneSettings Settings => _settings.Current;

    /// <summary>Reports startup progress so the tray tooltip can say "starting" rather than lying.</summary>
    public bool IsReady { get; private set; }

    public static JaneHost Create(Dispatcher dispatcher, IOverlayPresenter overlay)
    {
        var paths = new JanePaths();
        var settings = new SettingsStore(paths);
        var current = settings.Read();
        settings.StartWatching();

        // Engine selection comes from `bench`, not from a constant here. Re-running the bench
        // in another process is picked up on the next read; see SettingsStore.StartWatching.
        var recognizer = BuildRecognizer(paths, current.Speech);

        var vad = new SileroVadGate(ModelCatalog.SileroVad.ResolvePath(paths.Models));

        var capture = WasapiCapture.ForDefaultDevice(new AudioCaptureOptions() with
        {
            MaxCaptureDuration = TimeSpan.FromMilliseconds(current.Hotkey.MaxToggleDurationMs),
            DeviceId = current.MicrophoneDeviceId,
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

        // Settings, dictionary, instructions and history all live in one SQLite file, opened once
        // here. The migration runner brings the Phase 1 settings.json across on first open.
        var database = JaneDatabase.Open(paths);
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

        var orchestrator = new DictationOrchestrator(
            capture,
            recognizer,
            new SileroVoiceActivityGate(vad),
            formatter,
            injector,
            focus);

        var hotkeys = new LowLevelKeyboardHook(
            new HotkeyBinding(current.Hotkey.VirtualKey, []),
            current.Hotkey.Mode,
            new HotkeyOptions
            {
                MinimumHold = TimeSpan.FromMilliseconds(current.Hotkey.MinimumHoldMs),
                MaxDuration = TimeSpan.FromMilliseconds(current.Hotkey.MaxToggleDurationMs),
            });

        return new JaneHost(dispatcher, settings, capture, recognizer, vad, hotkeys, orchestrator, overlay, llm, database, dictionary, instructions, history);
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
    }

    /// <summary>Stops the hotkey firing without tearing the graph down.</summary>
    /// <remarks>
    /// The hook stays installed and the microphone stays open. Re-opening the device on unpause
    /// would reintroduce the device-open latency that keeping it open exists to avoid, and the
    /// idle cost of an open capture is a fraction of a percent of one core.
    /// </remarks>
    public void SetPaused(bool paused) => _paused = paused;

    public void SetMode(HotkeyMode mode) => _hotkeys.Rebind(_hotkeys.Binding, mode);

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
        // The presenter marshals to the dispatcher itself, so this can be called from the
        // pipeline's own thread without a hop here.
        var level = status.State is PipelineState.Arming or PipelineState.Listening
            ? _capture.CurrentLevel
            : 0f;

        _overlay.Show(status.ToOverlayStatus(level));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _hotkeys.HotkeyEvent -= OnHotkeyEvent;
        Orchestrator.StateChanged -= OnPipelineStateChanged;
        _hotkeys.Dispose();

        await Orchestrator.DisposeAsync();

        if (_llm is not null)
        {
            await _llm.DisposeAsync();
        }

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
