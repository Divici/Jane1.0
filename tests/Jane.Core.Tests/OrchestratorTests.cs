using System.Net;
using Jane.Core.Abstractions;
using Jane.Core.Modes;
using Jane.Core.Pipeline;
using Jane.Core.Text;

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
    public async Task ACaptureThatNeverHeardSoundIsAMicrophoneProblemNotSilenceFromTheUser()
    {
        // "No speech detected" was the verdict on 40 cold starts in the field log, and on every
        // one of them the user had spoken. A buffer of pure zeros is a device that was not
        // hearing -- muted, or a Bluetooth link still waking -- and saying so is what lets the
        // user fix it instead of repeating themselves at a microphone that is off.
        var harness = new Harness { SpeechDetected = false };
        harness.Source.SilentCapture = true;
        harness.Source.CaptureLength = TimeSpan.FromSeconds(4);

        await harness.DictateAsync();

        Assert.Equal(PipelineFailure.MicrophoneSilent, harness.Orchestrator.Status.Failure);
        Assert.Contains("muted", harness.Orchestrator.Status.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Recognizer.Calls);
    }

    [Fact]
    public async Task AShortSilentCaptureSaysTheMicrophoneWasStillStarting()
    {
        // 1.6 seconds of audio, all zero, is the shape of a key let go before a waking headset
        // delivered anything. The remedy is different from a muted microphone's, so the words are.
        var harness = new Harness { SpeechDetected = false };
        harness.Source.SilentCapture = true;
        harness.Source.CaptureLength = TimeSpan.FromMilliseconds(1_600);

        await harness.DictateAsync();

        Assert.Equal(PipelineFailure.MicrophoneSilent, harness.Orchestrator.Status.Failure);
        Assert.Contains("starting", harness.Orchestrator.Status.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMicrophoneReportingAFaultIsNamedInsteadOfBlamingTheUserForSilence()
    {
        var harness = new Harness { SpeechDetected = false };
        harness.Source.Error = "Capture on 'Jabra Link 380' failed: the device was removed.";

        await harness.DictateAsync();

        Assert.Equal(PipelineFailure.NoMicrophone, harness.Orchestrator.Status.Failure);
        Assert.Equal("Capture on 'Jabra Link 380' failed: the device was removed.", harness.Orchestrator.Status.Message);
    }

    [Fact]
    public async Task AQuietUserIsStillJustNoSpeech()
    {
        // The buffer carries a noise floor and the voice gate found no words in it. That is the
        // user, not the device, and the message must not send them off to check a microphone
        // that is working.
        var harness = new Harness { SpeechDetected = false };

        await harness.DictateAsync();

        Assert.Equal(PipelineFailure.NoSpeech, harness.Orchestrator.Status.Failure);
    }

    [Fact]
    public async Task AMicrophoneThatWillNotOpenAtStartupDoesNotStopTheSpeechEngineLoading()
    {
        // The two were coupled: an audio open that threw was wrapped as "the speech engine could
        // not be loaded", which blamed a model that was fine and hid the microphone message that
        // would have named the cause. A machine with no microphone still gets a loaded engine,
        // and the key press reports the microphone.
        var harness = new Harness { SpeechDetected = false };
        harness.Source.OpenThrows = new InvalidOperationException("Windows reports no default communications input device.");

        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Recognizer.IsLoaded);

        await harness.PressAndReleaseAsync();

        Assert.Equal(PipelineFailure.NoMicrophone, harness.Orchestrator.Status.Failure);
        Assert.Contains("no default communications input", harness.Orchestrator.Status.Message!, StringComparison.Ordinal);
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

    [Fact]
    public async Task ALiveSelectionRunsEditModeRatherThanDictating()
    {
        var harness = new Harness
        {
            Transcript = "make it shorter",
            Context = new DictationContext(
                new SelectionResult(true, "The quarterly review has been moved.", SelectionSource.Uia), []),
            Rewrite = "Review moved.",
        };

        await harness.DictateAsync();

        // The instruction must not be typed; the rewrite must be.
        Assert.Equal("Review moved.", Assert.Single(harness.Injector.Injected));
        Assert.Equal(0, harness.Formatter.Calls);
    }

    [Fact]
    public async Task AnUnreadableSelectionInjectsNothingAndSaysEditModeIsUnavailable()
    {
        // BLOCKER #8, at the pipeline level. Without this the words "make it shorter" are typed
        // into the user's document.
        var harness = new Harness
        {
            Transcript = "make it shorter",
            Context = new DictationContext(SelectionResult.Unreadable, []),
        };

        await harness.DictateAsync();

        Assert.Empty(harness.Injector.Injected);
        Assert.Equal(PipelineFailure.EditModeUnavailable, harness.Orchestrator.Status.Failure);
        Assert.Equal(OverlayState.EditModeUnavailable,
            harness.Orchestrator.Status.ToOverlayStatus().State);
    }

    [Fact]
    public async Task DeepContextTermsReachTheRecogniserAsHotwords()
    {
        // The Phase 1 bench measured biasing at 88% of greedy, so it is enabled and the terms
        // genuinely have to arrive.
        var harness = new Harness
        {
            Context = new DictationContext(SelectionResult.None, ["Kubernetes", "Cilium"]),
        };

        await harness.DictateAsync();

        Assert.Equal(["Kubernetes", "Cilium"], harness.Recognizer.LastOptions!.Hotwords);
    }

    [Fact]
    public async Task SendItStripsThePhraseAndSubmitsOnlyAfterInjection()
    {
        var harness = new Harness { Transcript = "tell them it is ready send it" };

        await harness.DictateAsync();

        Assert.Equal("tell them it is ready", Assert.Single(harness.Injector.Injected));
        Assert.Equal(["inject", "submit"], harness.Submitter.Order);
    }

    [Fact]
    public async Task SendItDoesNotSubmitWhenInjectionWasRefused()
    {
        // Submitting a form that never received the text is worse than not submitting.
        var harness = new Harness { Transcript = "ship it send it" };
        harness.Injector.Result = InjectionResult.Aborted(InjectionFailure.ModifierHeld, "Ctrl held");

        await harness.DictateAsync();

        Assert.DoesNotContain("submit", harness.Submitter.Order);
    }

    [Fact]
    public async Task DeepContextIsStartedAtKeyDownNotAfterTheUserStopsSpeaking()
    {
        // The whole reason UIA's round trips are affordable: they overlap with speech.
        var harness = new Harness();
        await harness.Orchestrator.StartAsync(TestContext.Current.CancellationToken);

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Pressed, TimeSpan.Zero, DateTimeOffset.Now));

        Assert.Equal(1, harness.ContextSource.BeginCount);
        Assert.Equal(0, harness.ContextSource.CollectCount);

        harness.Orchestrator.OnHotkey(new HotkeyEvent(HotkeyEventKind.Released, TimeSpan.FromSeconds(1), DateTimeOffset.Now));
        await harness.Orchestrator.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.ContextSource.CollectCount);
    }

    [Fact]
    public async Task ASecondDictationIntoTheSameWindowIsSpacedFromTheFirst()
    {
        var harness = new Harness();

        await harness.DictateAsync("Hello there.");
        await harness.DictateAsync("How are you?");

        // The bug this exists for produced "Hello there.How are you?" -- every sentence after the
        // first glued to its predecessor, in a tool whose point is not touching the keyboard.
        Assert.Equal(["Hello there.", " How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task MovingToADifferentWindowStartsCleanAgain()
    {
        var harness = new Harness();

        await harness.DictateAsync("Hello there.");
        harness.Focus.Current = new TargetWindow(9, 9, "chrome", "Chrome", "New tab");
        await harness.DictateAsync("How are you?");

        // Jane knows what it typed into Notepad; it knows nothing about where the caret sits in
        // Chrome, and a leading space in an empty search box is its own bug.
        Assert.Equal(["Hello there.", "How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task AFailedInjectionIsNotRememberedAsPrecedingText()
    {
        var harness = new Harness();
        harness.Injector.Result = InjectionResult.Aborted(InjectionFailure.TargetChanged, "focus moved");

        await harness.DictateAsync("Hello there.");
        harness.Injector.Result = null;
        await harness.DictateAsync("How are you?");

        // Nothing reached the window the first time, so there is nothing to be spaced from.
        Assert.Equal("How are you?", harness.Injector.Injected[^1]);
    }

    [Fact]
    public async Task AutomaticSpacingCanBeTurnedOff()
    {
        var harness = new Harness(new OrchestratorOptions { AutoSpace = false });

        await harness.DictateAsync("Hello there.");
        await harness.DictateAsync("How are you?");

        Assert.Equal(["Hello there.", "How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task ASpaceAlreadyBeforeTheCaretIsNotDoubled()
    {
        var harness = new Harness();

        await harness.DictateAsync("Hello there.");

        // The user pressed space themselves. Jane's memory still says the window ends in a full
        // stop; the window says otherwise, and the window is the one that is right.
        harness.Context = DictationContext.Empty with { PrecedingText = " " };
        await harness.DictateAsync("How are you?");

        Assert.Equal(["Hello there.", "How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task TextBeforeTheCaretIsSpacedFromEvenOnTheFirstDictation()
    {
        var harness = new Harness
        {
            Context = DictationContext.Empty with { PrecedingText = "d" },
        };

        await harness.DictateAsync("How are you?");

        // No memory at all -- Jane has typed nothing here -- but the application reported a letter
        // hard against the caret, so gluing a sentence to it would be wrong.
        Assert.Equal(" How are you?", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task ACaretAtTheStartOfTheDocumentGetsNoSpace()
    {
        var harness = new Harness();

        await harness.DictateAsync("Hello there.");

        // Select-all, delete, dictate again. Empty is a known answer, not a missing one.
        harness.Context = DictationContext.Empty with { PrecedingText = string.Empty };
        await harness.DictateAsync("How are you?");

        Assert.Equal("How are you?", harness.Injector.Injected[^1]);
    }

    [Fact]
    public async Task TypingOrClickingSinceTheLastDictationDiscardsTheMemory()
    {
        var harness = new Harness();

        await harness.DictateAsync("Hello there.");
        harness.Activity.UserDidSomething();
        await harness.DictateAsync("How are you?");

        // The window will not say what is before the caret and the user has touched it since, so
        // Jane no longer knows. Not knowing means adding nothing.
        Assert.Equal(["Hello there.", "How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task ActivityBeforeADictationDoesNotSpoilTheOneAfterIt()
    {
        var harness = new Harness();

        harness.Activity.UserDidSomething();
        await harness.DictateAsync("Hello there.");
        await harness.DictateAsync("How are you?");

        Assert.Equal(["Hello there.", " How are you?"], harness.Injector.Injected);
    }

    [Fact]
    public async Task SpokenNumbersAndSymbolsAreWrittenOutBeforeAnythingElseSeesThem()
    {
        var harness = new Harness();

        await harness.DictateAsync("three slash four");

        // Before the formatter, not after: the in-game route and the short-sentence bypass never
        // reach a language model, and a fraction has to come out right on those too.
        Assert.Equal("3/4", Assert.Single(harness.Formatter.Received));
        Assert.Equal("3/4", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task WhatTheRecogniserActuallyHeardIsKeptForTheRecord()
    {
        var harness = new Harness();

        await harness.DictateAsync("ticket one eight eight");

        // History shows the raw transcript beside the final text. If that were the converted
        // text, "why did Jane write 188" would have no answer anywhere.
        Assert.Equal("ticket one eight eight", Assert.Single(harness.Formatter.Contexts).Recognised);
    }

    [Fact]
    public async Task SpokenFormsCanBeSwitchedOffOnARunningPipeline()
    {
        var harness = new Harness();
        harness.Orchestrator.SpokenForms = SpokenFormOptions.Off;

        await harness.DictateAsync("three slash four");

        Assert.Equal("three slash four", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task SpokenFormsAreSeededFromTheOptions()
    {
        var harness = new Harness(new OrchestratorOptions
        {
            SpokenForms = new SpokenFormOptions(NumberStyle.AsSpoken, Symbols: true),
        });

        await harness.DictateAsync("three slash four");

        Assert.Equal("three/four", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task ACorrectionSpokenOverASelectionIsWrittenOutToo()
    {
        var harness = new Harness
        {
            Transcript = "the ratio is three slash four",
            Context = new DictationContext(
                new SelectionResult(true, "the ratio is a half", SelectionSource.Uia), []),
        };

        await harness.DictateAsync();

        // No command word, so this replaces the selection with what was said -- and what was
        // said is typed text like any other.
        Assert.Equal("the ratio is 3/4", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task AMissingSpeechModelIsReportedRatherThanBreakingStartUp()
    {
        // A machine Jane has just been installed on has no models at all. Starting must still
        // succeed -- it is what puts the tray icon, the settings window and onboarding on screen,
        // and onboarding is the only thing that can download them.
        var harness = new Harness { RecognizerLoadThrows = new FileNotFoundException("encoder.int8.onnx") };

        await harness.DictateAsync();

        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.Equal(PipelineFailure.EngineUnavailable, harness.Orchestrator.Status.Failure);
        Assert.Empty(harness.Injector.Injected);
    }

    [Fact]
    public async Task AnEngineThatCouldNotLoadIsTriedAgainOnTheNextKeyPress()
    {
        // The model was missing at launch and has been downloaded since. Nothing told the
        // pipeline -- and nothing should have to. The old behaviour remembered the first failure
        // for the life of the process, so a freshly installed Jane could not dictate until it was
        // quit and started again, with nothing on screen to say so.
        var harness = new Harness { RecognizerLoadThrows = new FileNotFoundException("encoder.int8.onnx") };
        await harness.Orchestrator.StartAsync(CancellationToken.None);

        await harness.PressAndReleaseAsync();
        Assert.Equal(PipelineFailure.EngineUnavailable, harness.Orchestrator.Status.Failure);

        harness.RecognizerLoadThrows = null;
        await harness.PressAndReleaseAsync();

        Assert.Equal(PipelineState.Idle, harness.Orchestrator.Status.State);
        Assert.Equal("hello world", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task AnEngineThatLoadedIsNeverLoadedTwice()
    {
        // The retry exists for a failed load. A successful one costs over a second and holds
        // hundreds of megabytes; doing it again on every key press would be the opposite bug.
        var harness = new Harness();

        await harness.DictateAsync("one moment");
        await harness.PressAndReleaseAsync();
        await harness.Orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(1, harness.Recognizer.LoadAttempts);
    }

    [Fact]
    public async Task AMissingVoiceDetectionModelFailsTheDictationInsteadOfHangingIt()
    {
        // Voice detection used to be built before anything else, so a missing model stopped the
        // whole application from composing. It is built on first use now, which moves the failure
        // here -- and an exception escaping this point left the pill on "Listening" for ever.
        var harness = new Harness
        {
            VadThrows = new SpeechEngineUnavailableException("The voice detection model has not been downloaded yet."),
        };

        await harness.DictateAsync();

        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.Equal(PipelineFailure.EngineUnavailable, harness.Orchestrator.Status.Failure);
        Assert.Contains("voice detection", harness.Orchestrator.Status.Message, StringComparison.OrdinalIgnoreCase);

        // And the next press works once it has arrived.
        harness.VadThrows = null;
        await harness.DictateAsync();

        Assert.Equal("hello world", Assert.Single(harness.Injector.Injected));
    }

    [Fact]
    public async Task NoFailureInsideADictationCanLeaveThePipelineStuck()
    {
        // The last line of defence. Whatever throws, the pipeline must come to rest in a state
        // the next key press can start from, with a message that is not an exception's name.
        var harness = new Harness { VadThrows = new InvalidOperationException("native library exploded") };

        await harness.DictateAsync();

        Assert.Equal(PipelineState.Failed, harness.Orchestrator.Status.State);
        Assert.DoesNotContain("Exception", harness.Orchestrator.Status.Message, StringComparison.Ordinal);

        harness.VadThrows = null;
        await harness.DictateAsync();

        Assert.Equal("hello world", Assert.Single(harness.Injector.Injected));
    }
}
