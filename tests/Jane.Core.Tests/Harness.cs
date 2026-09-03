using System.Net;
using Jane.Core.Abstractions;
using Jane.Core.Modes;
using Jane.Core.Pipeline;

namespace Jane.Core.Tests;

/// <summary>
/// A whole dictation pipeline built from fakes, so every routing and failure rule can be asserted
/// without a microphone, a keyboard hook or a target window.
/// </summary>
internal sealed class Harness
{
    private readonly List<PipelineState> _states = [];

    public Harness()
    {
        Source = new FakeAudioSource();
        Recognizer = new FakeRecognizer(this);
        Vad = new FakeVad(this);
        Formatter = new FakeFormatter();
        Injector = new FakeInjector();
        Focus = new FakeFocusTracker();
        Sockets = new SocketRecorder();

        ContextSource = new FakeContextSource(this);
        Submitter = new RecordingSubmitter(Injector);

        Orchestrator = new DictationOrchestrator(
            Source, Recognizer, Vad, Formatter, Injector, Focus,
            new OrchestratorOptions { EngineReadyTimeout = TimeSpan.FromSeconds(10) },
            ContextSource,
            new FakeRewriter(this),
            Submitter);

        Orchestrator.StateChanged += (_, status) =>
        {
            lock (_states)
            {
                _states.Add(status.State);
            }
        };
    }

    public DictationOrchestrator Orchestrator { get; }

    public FakeAudioSource Source { get; }

    public FakeRecognizer Recognizer { get; }

    public FakeVad Vad { get; }

    public FakeFormatter Formatter { get; }

    public FakeInjector Injector { get; }

    public FakeFocusTracker Focus { get; }

    public SocketRecorder Sockets { get; init; }

    public FakeContextSource ContextSource { get; }

    public RecordingSubmitter Submitter { get; }

    /// <summary>What Deep Context reports: selection and hotwords.</summary>
    public DictationContext Context { get; init; } = DictationContext.Empty;

    /// <summary>What a selection rewrite returns.</summary>
    public string Rewrite { get; init; } = "rewritten";

    public string Transcript { get; init; } = "hello world";

    public bool SpeechDetected { get; init; } = true;

    public Exception? RecognizerThrows { get; init; }

    public Task? RecognizerGate { get; init; }

    public Task? RecognizerLoadGate { get; init; }

    /// <summary>Where the pipeline is allowed to write. Asserted to contain no audio.</summary>
    public string? StateDirectory { get; init; }

    public IReadOnlyList<PipelineState> States
    {
        get
        {
            lock (_states)
            {
                return [.. _states];
            }
        }
    }

    /// <summary>Press, hold, release, and wait for the pipeline to settle.</summary>
    public async Task DictateAsync(string? transcript = null)
    {
        if (transcript is not null)
        {
            Recognizer.Transcript = transcript;
        }

        await Orchestrator.StartAsync(CancellationToken.None);
        _states.Clear();

        Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));
        await Orchestrator.WaitForIdleAsync(CancellationToken.None);
    }

    internal sealed class FakeAudioSource : IAudioSource
    {
        /// <summary>The last routing applied. Nothing here opens a device, so it is only recorded.</summary>
        public MicrophoneRouting? Routing { get; private set; }

        public void Reconfigure(MicrophoneRouting routing) => Routing = routing;

        public AudioSourceState State { get; private set; } = new(false, false, "Fake mic", null);

        public event EventHandler<AudioSourceState>? StateChanged;

        public bool Opened { get; private set; }

        public int ArmCount { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            Opened = true;
            State = State with { IsOpen = true };
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void Arm()
        {
            ArmCount++;
            State = State with { IsCapturing = true };
        }

        public CapturedAudio Stop(CaptureStopReason reason)
        {
            State = State with { IsCapturing = false };

            // One second of a quiet tone: long enough to be a plausible utterance, and non-zero
            // so a fake VAD is not the only thing standing between silence and the recogniser.
            var samples = new float[AudioFormat.SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(Math.Sin(i * 0.05) * 0.2);
            }

            return new CapturedAudio(samples, AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(500)), reason, DateTimeOffset.Now);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeRecognizer(Harness harness) : ISpeechRecognizer
    {
        public string EngineId => "fake";

        public bool IsLoaded { get; private set; }

        public bool Loaded => IsLoaded;

        public int Calls { get; private set; }

        // Read through to the harness so a `new Harness { Transcript = ... }` initialiser, which
        // runs after this constructor, is still honoured.
        private string? _transcript;

        public string Transcript
        {
            get => _transcript ?? harness.Transcript;
            set => _transcript = value;
        }

        /// <summary>The options the last transcription actually received, so biasing can be asserted.</summary>
        public RecognitionOptions? LastOptions { get; private set; }

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            if (harness.RecognizerLoadGate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            IsLoaded = true;
        }

        public async Task<RecognitionResult> TranscribeAsync(
            ReadOnlyMemory<float> pcm16k, RecognitionOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            LastOptions = options;

            if (harness.RecognizerGate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            if (harness.RecognizerThrows is { } ex)
            {
                throw ex;
            }

            return new RecognitionResult(
                Transcript, [],
                new RecognitionTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80)),
                "greedy_search");
        }

        public void Dispose()
        {
        }
    }

    internal sealed class FakeContextSource(Harness harness) : IDictationContextSource
    {
        public int BeginCount { get; private set; }

        public int CollectCount { get; private set; }

        public void BeginRead(TargetWindow target) => BeginCount++;

        public Task<DictationContext> CollectAsync(TargetWindow target, CancellationToken cancellationToken)
        {
            CollectCount++;
            return Task.FromResult(harness.Context);
        }
    }

    internal sealed class FakeRewriter(Harness harness) : ISelectionRewriter
    {
        public Task<string> RewriteAsync(
            string selection, string instruction, FormattingContext context, CancellationToken cancellationToken) =>
            Task.FromResult(harness.Rewrite);
    }

    internal sealed class RecordingSubmitter(FakeInjector injector) : ISubmitter
    {
        public List<string> Order { get; } = [];

        public Task SubmitAsync(TargetWindow target, CancellationToken cancellationToken)
        {
            if (injector.Injected.Count > 0)
            {
                Order.Add("inject");
            }

            Order.Add("submit");
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeVad(Harness harness) : IVoiceActivityGate
    {
        public VoiceActivityResult Process(ReadOnlyMemory<float> samples) =>
            new(harness.SpeechDetected, harness.SpeechDetected ? samples : ReadOnlyMemory<float>.Empty);
    }

    internal sealed class FakeFormatter : ITranscriptFormatter
    {
        public int Calls { get; private set; }

        public Task<string> FormatAsync(string transcript, FormattingContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(transcript);
        }
    }

    internal sealed class FakeInjector : ITextInjector
    {
        public List<string> Injected { get; } = [];

        public TargetWindow LastTarget { get; private set; } = TargetWindow.None;

        public InjectionResult? Result { get; set; }

        public Task<InjectionResult> InjectAsync(string text, TargetWindow target, CancellationToken cancellationToken)
        {
            LastTarget = target;

            if (Result is { Succeeded: false } failure)
            {
                return Task.FromResult(failure);
            }

            Injected.Add(text);
            return Task.FromResult(new InjectionResult(true, InjectionStrategy.Unicode, text.Length, TimeSpan.FromMilliseconds(12)));
        }
    }

    internal sealed class FakeFocusTracker : IFocusTracker
    {
        public TargetWindow Current { get; set; } = new(1, 2, "notepad", "Notepad", "Untitled");

        public TargetWindow GetForegroundWindow() => Current;
    }
}

/// <summary>Records every endpoint the pipeline would connect to, so loopback can be asserted.</summary>
internal sealed class SocketRecorder
{
    private readonly List<IPEndPoint> _endpoints = [];

    public IReadOnlyList<IPEndPoint> Endpoints
    {
        get
        {
            lock (_endpoints)
            {
                return [.. _endpoints];
            }
        }
    }

    public void Record(IPEndPoint endpoint)
    {
        lock (_endpoints)
        {
            _endpoints.Add(endpoint);
        }
    }
}
