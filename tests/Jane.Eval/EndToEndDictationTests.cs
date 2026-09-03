using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Core.Audio;
using Jane.Core.Pipeline;
using Jane.Core.Platform;
using Jane.Core.Text;
using Jane.Speech;
using Jane.Windows.Automation;
using Jane.Windows.Injection;

namespace Jane.Eval;

/// <summary>
/// Phase 5's acceptance, automated.
/// </summary>
/// <remarks>
/// The plan states it as "hold Right Ctrl, speak, release, raw text appears in Notepad". Two of
/// those cannot be automated on a build machine: nobody can speak into the microphone, and
/// pressing a real hotkey means racing whatever else has focus.
/// <para>
/// Everything else is real. The recogniser is the shipped <see cref="ParakeetRecognizer"/> over
/// the model in <c>%LOCALAPPDATA%\Jane\models</c>, the voice gate is the real
/// <see cref="SileroVadGate"/>, the injector is the real Win32 <see cref="SendInputInjector"/>
/// with the real <see cref="ModifierGate"/>, and the target is a real Notepad window whose text
/// is read back through Win32. Only the microphone is replaced, by a fixture WAV -- and Phase 2
/// verified the capture path against the real device separately.
/// </para>
/// </remarks>
[Collection("EndToEnd")]
public sealed class EndToEndDictationTests
{
    private static string ModelRoot => new JanePaths().Models;

    private static string ParakeetDirectory => ModelCatalog.ParakeetV2Int8.ResolvePath(ModelRoot);

    private static bool ModelsInstalled =>
        ModelCatalog.ParakeetComponents.All(c => File.Exists(Path.Combine(ParakeetDirectory, c)))
        && File.Exists(ModelCatalog.SileroVad.ResolvePath(ModelRoot));

    [Fact]
    public async Task HoldSpeakRelease_PutsRealPunctuatedTextIntoARealWin32TextBox()
    {
        Assert.SkipUnless(ModelsInstalled, "Speech models are not downloaded.");
        var fixturePath = FindFixture("prose-01.wav");
        Assert.SkipWhen(fixturePath is null, "Fixture corpus has not been built. Run `bench -- fixtures`.");

        using var foreground = ForegroundLock.Acquire();
        using var notepad = Win32EditHarness.Create();
        Assert.SkipWhen(notepad is null, "No interactive desktop: the target window could not take foreground.");

        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));
        using var vad = new SileroVadGate(ModelCatalog.SileroVad.ResolvePath(ModelRoot));

        var focus = new FocusedAppIdentity();
        var injector = new SendInputInjector(focus, focus, new Win32SendInput(),
            new ModifierGate(new Win32AsyncKeyState()));

        var orchestrator = new DictationOrchestrator(
            new FixtureAudioSource(WaveFile.Read(fixturePath!)),
            recognizer,
            new SileroGateAdapter(vad),
            new PassthroughFormatter(),
            injector,
            new FixedTarget(notepad!.Target));

        await using (orchestrator)
        {
            await orchestrator.StartAsync(TestContext.Current.CancellationToken);

            notepad.Focus();
            orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
            orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(3), DateTimeOffset.Now));
            await orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(PipelineState.Idle, orchestrator.Status.State);

            var typed = await notepad.ReadTextAsync(TestContext.Current.CancellationToken);

            Assert.False(string.IsNullOrWhiteSpace(typed), "Nothing was typed into the target window.");

            // Raw Parakeet output, with no LLM anywhere in this path. It arrives already
            // capitalised and punctuated, which is the whole reason the in-game route can skip
            // the LLM and still produce presentable text.
            Assert.True(char.IsUpper(typed.TrimStart()[0]), $"Expected a capitalised first character: {typed}");
            Assert.Contains(".", typed, StringComparison.Ordinal);

            // Accuracy is a rate, not an exact match. The engine measured 6.6% WER on this corpus
            // and does make real errors -- on this fixture it hears "poll request" for "pull
            // request", which is exactly the class of miss the Phase 10 dictionary and hotword
            // biasing exist to fix. Asserting an exact substring would make this test a lottery
            // on one word; asserting the rate is the gate that actually means something.
            const string Reference = "I'll take a look at the pull request this afternoon and leave some comments on the parts I'm unsure about.";
            var wer = WordErrorRate.Rate(Reference, typed);
            Assert.True(wer < 0.25, $"End-to-end WER was {wer:P1}, over the 25% gate. Got: {typed}");
        }
    }

    [Fact]
    public async Task SilentAudio_TypesNothingAndReportsNoSpeech()
    {
        Assert.SkipUnless(ModelsInstalled, "Speech models are not downloaded.");

        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));
        using var vad = new SileroVadGate(ModelCatalog.SileroVad.ResolvePath(ModelRoot));

        var injector = new RecordingInjector();
        var orchestrator = new DictationOrchestrator(
            new FixtureAudioSource(new float[AudioFormat.SampleRate * 2]),
            recognizer,
            new SileroGateAdapter(vad),
            new PassthroughFormatter(),
            injector,
            new FixedTarget(new TargetWindow(1, 2, "notepad", "Notepad", "Untitled")));

        await using (orchestrator)
        {
            await orchestrator.StartAsync(TestContext.Current.CancellationToken);
            orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
            orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(2), DateTimeOffset.Now));
            await orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

            // Two seconds of digital silence must cost nothing and say so plainly.
            Assert.Empty(injector.Injected);
            Assert.Equal(PipelineFailure.NoSpeech, orchestrator.Status.Failure);
        }
    }

    private static string? FindFixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "audio", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>Replays a fixture WAV in place of the microphone.</summary>
    private sealed class FixtureAudioSource(float[] samples) : IAudioSource
    {
        public void Reconfigure(MicrophoneRouting routing)
        {
        }

        public AudioSourceState State { get; private set; } = new(false, false, "Fixture", null);

        public event EventHandler<AudioSourceState>? StateChanged;

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            State = State with { IsOpen = true };
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void Arm() => State = State with { IsCapturing = true };

        public CapturedAudio Stop(CaptureStopReason reason)
        {
            State = State with { IsCapturing = false };
            return new CapturedAudio(samples, 0, reason, DateTimeOffset.Now);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SileroGateAdapter(SileroVadGate gate) : IVoiceActivityGate
    {
        public VoiceActivityResult Process(ReadOnlyMemory<float> samples)
        {
            var result = gate.Process(samples);
            return new VoiceActivityResult(result.ContainsSpeech, result.Samples);
        }
    }

    private sealed class FixedTarget(TargetWindow target) : IFocusTracker
    {
        public TargetWindow GetForegroundWindow() => target;
    }

    private sealed class RecordingInjector : ITextInjector
    {
        public List<string> Injected { get; } = [];

        public Task<InjectionResult> InjectAsync(string text, TargetWindow target, CancellationToken cancellationToken)
        {
            Injected.Add(text);
            return Task.FromResult(new InjectionResult(true, InjectionStrategy.Unicode, text.Length, TimeSpan.Zero));
        }
    }
}

