using System.Net;
using Jane.Core.Abstractions;
using Jane.Core.Pipeline;

namespace Jane.Core.Tests;

/// <summary>
/// The pipeline, driven entirely through fakes. Every rule that decides whether text reaches the
/// user's window lives in <see cref="DictationOrchestrator"/> precisely so it can be asserted
/// here rather than only by holding a key down and hoping.
/// </summary>
public sealed class OrchestratorTests
{
    [Fact]
    public async Task FullPipelineProducesExactlyOneInjectionPerPress()
    {
        var harness = new Harness();

        await harness.DictateAsync("hello world");

        var injected = Assert.Single(harness.Injector.Injected);
        Assert.Equal("hello world", injected);
        Assert.Equal(PipelineState.Idle, harness.Orchestrator.Status.State);
    }

    [Fact]
    public async Task StateChangedFiresForEveryTransition()
    {
        var harness = new Harness();

        await harness.DictateAsync("hello");

        // The overlay subscribes to exactly this sequence; a missing transition is a pill that
        // sticks on the previous state.
        Assert.Equal(
            [
                PipelineState.Arming,
                PipelineState.Listening,
                PipelineState.Transcribing,
                PipelineState.Formatting,
                PipelineState.Injecting,
                PipelineState.Idle,
            ],
            harness.States);
    }

    [Fact]
    public async Task EmptyTranscriptInjectsNothingAndSkipsLlm()
    {
        var harness = new Harness { Transcript = "   " };

        await harness.DictateAsync();

        Assert.Empty(harness.Injector.Injected);
        Assert.Equal(0, harness.Formatter.Calls);
        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.Equal(PipelineFailure.NoSpeech, harness.Orchestrator.Status.Failure);
    }

    [Fact]
    public async Task VadRejectingTheBufferSkipsTranscriptionEntirely()
    {
        // The models must not spin up for a buffer with no speech in it. This is what makes a
        // stray press of a common game bind free rather than merely quiet.
        var harness = new Harness { SpeechDetected = false };

        await harness.DictateAsync();

        Assert.Equal(0, harness.Recognizer.Calls);
        Assert.Empty(harness.Injector.Injected);
        Assert.Equal(PipelineFailure.NoSpeech, harness.Orchestrator.Status.Failure);
    }

    [Fact]
    public async Task CancellationMidListeningYieldsZeroInjections()
    {
        var harness = new Harness();
        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Cancelled, TimeSpan.FromSeconds(1), DateTimeOffset.Now));
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Injector.Injected);
        Assert.Equal(0, harness.Recognizer.Calls);
        Assert.Contains(PipelineState.Cancelled, harness.States);
    }

    [Fact]
    public async Task SubMinimumHoldProducesNothingAtAll()
    {
        // Not even an error toast: a hold under the minimum is treated as though it never
        // happened, because on a common game bind it usually did not.
        var harness = new Harness();
        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.TooShort, TimeSpan.FromMilliseconds(120), DateTimeOffset.Now));
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Injector.Injected);
        Assert.Equal(0, harness.Recognizer.Calls);
        Assert.DoesNotContain(PipelineState.Failed, harness.States);
        Assert.Equal(PipelineState.Idle, harness.Orchestrator.Status.State);
    }

    [Fact]
    public async Task RecognizerThrowingMidRunLandsInFailedWithAudioDiscarded()
    {
        var harness = new Harness { RecognizerThrows = new InvalidOperationException("onnx session died") };

        await harness.DictateAsync();

        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.Equal(PipelineFailure.RecognitionFailed, harness.Orchestrator.Status.Failure);
        Assert.Empty(harness.Injector.Injected);

        // The message shown to the user must never be an exception's ToString.
        Assert.DoesNotContain("InvalidOperationException", harness.Orchestrator.Status.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingEngineIsItsOwnFailureWithItsOwnRemedy()
    {
        var harness = new Harness { RecognizerThrows = new SpeechEngineUnavailableException("model not downloaded") };

        await harness.DictateAsync();

        Assert.Equal(PipelineFailure.EngineUnavailable, harness.Orchestrator.Status.Failure);
    }

    [Fact]
    public async Task SecondKeyDownDuringTranscribingIsIgnored()
    {
        var gate = new TaskCompletionSource();
        var harness = new Harness { RecognizerGate = gate.Task };
        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));
        await harness.Orchestrator.WaitForStateAsync(PipelineState.Transcribing, TestContext.Current.CancellationToken);

        // A second press while a pipeline is in flight must not start another one, or two
        // dictations race to inject into the same window.
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));

        gate.SetResult();
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Single(harness.Injector.Injected);
        Assert.Equal(1, harness.Recognizer.Calls);
        Assert.Equal(1, harness.Source.ArmCount);
    }

    [Fact]
    public async Task InjectionAbortIsReportedRatherThanSwallowed()
    {
        var harness = new Harness();
        harness.Injector.Result = InjectionResult.Aborted(InjectionFailure.ModifierHeld, "Right Ctrl still held");

        await harness.DictateAsync("hello");

        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.Equal(PipelineFailure.InjectionAborted, harness.Orchestrator.Status.Failure);
        Assert.Contains("Ctrl", harness.Orchestrator.Status.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetWindowIsCapturedAtKeyDownNotAtInjectionTime()
    {
        // Capturing at injection time would send the text wherever focus happened to land while
        // the user was speaking.
        var harness = new Harness();
        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);
        harness.Focus.Current = new TargetWindow(101, 7, "notepad", "Notepad", "Untitled");

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Focus.Current = new TargetWindow(202, 9, "chrome", "Chrome_WidgetWin_1", "Bank");
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(101, harness.Injector.LastTarget.Handle);
        Assert.Equal("notepad", harness.Injector.LastTarget.ProcessName);
    }

    [Fact]
    public async Task EngineIsPreloadedSoTheFirstDictationDoesNotPayColdInit()
    {
        // Cold session-init measured 1.4 s in Phase 1's bench. Preloading is what keeps that off
        // the dictation path; without it the plan's whole latency budget is fiction.
        var harness = new Harness();

        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(harness.Recognizer.Loaded);
        Assert.True(harness.Source.Opened);
    }

    [Fact]
    public async Task HotkeyBeforeTheEngineIsReadyWaitsRatherThanFailing()
    {
        var loadGate = new TaskCompletionSource();
        var harness = new Harness { RecognizerLoadGate = loadGate.Task };

        var start = harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));
        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));

        loadGate.SetResult();
        await start;
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        // The dictation is not lost; it simply waited for the engine.
        Assert.Single(harness.Injector.Injected);
    }

    [Fact]
    public async Task DictationPipelineMakesNoNonLocalhostRequests()
    {
        // "Recreate it locally" is the brief's core promise. This asserts it rather than trusting
        // it: every socket the pipeline opens must be loopback.
        var recorder = new SocketRecorder();
        var harness = new Harness { Sockets = recorder };

        await harness.DictateAsync("hello");

        Assert.All(recorder.Endpoints, endpoint =>
            Assert.True(IPAddress.IsLoopback(endpoint.Address),
                $"The dictation path opened a socket to {endpoint}, which is not loopback."));
    }

    [Fact]
    public async Task NoAudioPersisted()
    {
        // Audio is never written to disk outside the fixture corpus. A dictation tool that
        // silently kept recordings would be a very different product from the one described.
        var root = Directory.CreateTempSubdirectory("jane-no-audio-test");
        try
        {
            var harness = new Harness { StateDirectory = root.FullName };

            await harness.DictateAsync("hello world");

            var suspicious = Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f) is ".wav" or ".pcm" or ".raw" or ".flac" or ".mp3" or ".opus")
                .ToArray();

            Assert.Empty(suspicious);

            // Nor may PCM leak into a log as text.
            foreach (var file in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
                Assert.DoesNotContain("PCM", text, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AudioBufferIsReleasedAfterEveryTerminalState()
    {
        var harness = new Harness { RecognizerThrows = new InvalidOperationException("boom") };

        await harness.DictateAsync();

        Assert.Null(harness.Orchestrator.DebugPendingAudio);
    }

    [Fact]
    public void EveryFailureHasAUserFacingMessage()
    {
        foreach (var failure in Enum.GetValues<PipelineFailure>().Where(f => f != PipelineFailure.None))
        {
            var message = PipelineStatus.DefaultMessageFor(failure);
            Assert.False(string.IsNullOrWhiteSpace(message), $"{failure} has no message.");
            Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryPipelineStateMapsToAnOverlayState()
    {
        foreach (var state in Enum.GetValues<PipelineState>())
        {
            var overlay = new PipelineStatus(state, PipelineFailure.NoSpeech, "msg").ToOverlayStatus();
            Assert.True(Enum.IsDefined(overlay.State));
        }

        // Transcribing and Formatting deliberately collapse to one visual.
        Assert.Equal(
            new PipelineStatus(PipelineState.Transcribing).ToOverlayStatus().State,
            new PipelineStatus(PipelineState.Formatting).ToOverlayStatus().State);
    }
}
