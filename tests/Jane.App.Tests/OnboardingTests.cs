using System.Net;
using System.Security.Cryptography;
using Jane.App.Composition;
using Jane.App.Onboarding;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Models;
using Jane.Speech;

namespace Jane.App.Tests;

/// <summary>
/// First run has to work with no instructions and no shell, on a machine that has none of the
/// models yet. These tests drive it end to end with a stubbed provisioner, and then check the
/// three ways a download can hard-fail -- offline, a checksum that does not match, and a disk
/// with no room -- each reach the user as a remedy they can act on rather than as a dead bar.
/// </summary>
public sealed class OnboardingTests
{
    [Fact]
    public async Task OnboardingCompletesEndToEndWithAStubbedDownloader()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        Assert.False(jane.ReopenSettings().OnboardingComplete);

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun(dictation: _ => Task.FromResult<string?>("Hello Jane, this is a test."));
            var model = window.Model;

            Assert.Equal(OnboardingStep.Welcome, model.Step);
            Assert.False(model.CanGoBack);

            // 1. Welcome -> Microphone
            await model.NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(OnboardingStep.Microphone, model.Step);

            model.Microphone = model.Microphones.Single(m => m.Id == "mic-1");
            Assert.True(model.HeardYou, "The fake capture reports a level, so the check should pass.");
            Assert.True(model.CanGoNext);

            // 2. Microphone -> Models
            await model.NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(OnboardingStep.Models, model.Step);
            Assert.False(model.RequiredModelsReady);
            Assert.False(model.CanGoNext);

            await model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            Assert.True(model.RequiredModelsReady);
            Assert.True(model.CanGoNext);

            // The language models are pulled here too. Onboarding does not leave a manual
            // `ollama pull` as homework -- that is a stated requirement of the phase.
            Assert.Contains("pull:" + LlmModelCatalog.Gpu.Tag, jane.Provisioner.Calls);
            Assert.Contains("pull:" + LlmModelCatalog.Cpu.Tag, jane.Provisioner.Calls);
            Assert.Contains("ensure:" + ModelCatalog.ParakeetV2Int8.Id, jane.Provisioner.Calls);
            Assert.Contains("ensure:" + ModelCatalog.SileroVad.Id, jane.Provisioner.Calls);

            // 3. Models -> Hotkey
            await model.NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(OnboardingStep.Hotkey, model.Step);

            Assert.Equal(HotkeyVerdict.Warned, model.HotkeyStatus.Verdict);
            Assert.True(model.TryRebind(new HotkeyBinding(0x7C, [])));
            Assert.Equal(HotkeyVerdict.Accepted, model.HotkeyStatus.Verdict);

            // 4. Hotkey -> a real dictation into the scratch box
            await model.NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(OnboardingStep.TestDictation, model.Step);

            await model.RunTestDictationAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Hello Jane", model.ScratchText, StringComparison.Ordinal);
            Assert.True(model.HasDictated);

            // 5. Done
            await model.NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(OnboardingStep.Done, model.Step);

            await model.FinishAsync(TestContext.Current.CancellationToken);
            Assert.True(model.IsComplete);
        });

        var stored = jane.ReopenSettings();
        Assert.True(stored.OnboardingComplete);
        Assert.Equal(0x7C, stored.Hotkey.VirtualKey);
        Assert.Equal("mic-1", stored.MicrophoneDeviceId);
    }

    [Fact]
    public async Task OnboardingFinishesWhenTheLanguageModelsCannotBePulledAtAll()
    {
        // The state this machine was actually in. No Ollama runtime is installed, so every pull
        // fails, and if that blocked the wizard the completion flag would never be written -- which
        // would have looked exactly like the replay bug and been a second cause of it.
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        jane.Provisioner.FailPullsWith = new InvalidOperationException("No Ollama server is running.");

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            var model = window.Model;

            await model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            // The two required models are the speech weights. The language models are extras: they
            // add transcript cleanup, and dictation works without them.
            Assert.True(model.RequiredModelsReady);
            Assert.True(model.CanGoNext);
            Assert.Contains(model.Models, m => m.Error is not null);

            await model.GoToAsync(OnboardingStep.Done, TestContext.Current.CancellationToken);
            await model.FinishAsync(TestContext.Current.CancellationToken);

            Assert.True(model.IsComplete);
        });

        var stored = jane.ReopenSettings();
        Assert.True(stored.OnboardingComplete);
        Assert.False(
            StartupPolicy.Decide(stored, skipRequested: false).ShouldRunOnboarding,
            "A finished wizard must not reopen on the next launch, pulls or no pulls.");
    }

    [Fact]
    public async Task OfflineMessagingSurfacesWithItsRemedyAndARetry()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        jane.Provisioner.FailWith = new ModelDownloadException(
            ModelDownloadFailure.Offline, "Could not reach github.com to download parakeet.");

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            var model = window.Model;

            await model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            Assert.False(model.RequiredModelsReady);
            Assert.False(model.CanGoNext);

            var parakeet = model.Models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);
            Assert.NotNull(parakeet.Error);
            Assert.Equal(ModelDownloadFailure.Offline, parakeet.Error!.Failure);
            Assert.Contains("network connection", parakeet.ErrorRemedy!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("resumes where it stopped", parakeet.ErrorRemedy!, StringComparison.OrdinalIgnoreCase);
            Assert.True(parakeet.CanRetry);

            // The summary says it out loud too, so the state is not only on one row.
            Assert.Contains("could not", model.ModelSummary, StringComparison.OrdinalIgnoreCase);

            jane.Provisioner.FailWith = null;
            await model.RetryFailedAsync(TestContext.Current.CancellationToken);

            Assert.True(model.RequiredModelsReady);
            Assert.Null(parakeet.Error);
        });
    }

    [Fact]
    public async Task AChecksumMismatchIsShownAsItsOwnFailureAndRetries()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        jane.Provisioner.FailWith = new ModelDownloadException(
            ModelDownloadFailure.ChecksumMismatch, "parakeet hashed to abc, expected 157c.");

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            var model = window.Model;

            await model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            var parakeet = model.Models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);
            Assert.Equal(ModelDownloadFailure.ChecksumMismatch, parakeet.Error!.Failure);
            Assert.Contains("pinned checksum", parakeet.ErrorRemedy!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("discarded", parakeet.ErrorRemedy!, StringComparison.OrdinalIgnoreCase);

            // A second failure must not become a different message -- repeated checksum
            // mismatches are their own documented case.
            await parakeet.Download.ExecuteAsync(null);
            Assert.Equal(ModelDownloadFailure.ChecksumMismatch, parakeet.Error!.Failure);

            jane.Provisioner.FailWith = null;
            await parakeet.Download.ExecuteAsync(null);

            Assert.True(parakeet.IsInstalled);
            Assert.Null(parakeet.Error);
        });
    }

    [Fact]
    public async Task ADiskFullFailureHasItsOwnRemedy()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        jane.Provisioner.FailWith = new ModelDownloadException(
            ModelDownloadFailure.DiskFull, "Ran out of disk space downloading parakeet.");

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            await window.Model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await window.Model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            var parakeet = window.Model.Models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);
            Assert.Equal(ModelDownloadFailure.DiskFull, parakeet.Error!.Failure);
            Assert.Contains("free space", parakeet.ErrorRemedy!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JANE_MODEL_DIR", parakeet.ErrorRemedy!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesOnRelaunch()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            // First run: the connection drops half way through.
            jane.Provisioner.FailWith = new ModelDownloadException(
                ModelDownloadFailure.Offline, "Could not reach github.com.");

            var first = jane.OpenFirstRun();
            await first.Model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await first.Model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            Assert.False(first.Model.RequiredModelsReady);
            Assert.True(jane.Provisioner.AlreadyDownloaded[ModelCatalog.ParakeetV2Int8.Id] > 0);

            // Relaunch: a brand new window over the same half-downloaded model directory.
            jane.Provisioner.FailWith = null;

            var second = jane.OpenFirstRun();
            await second.Model.GoToAsync(OnboardingStep.Models, TestContext.Current.CancellationToken);
            await second.Model.DownloadEverythingAsync(TestContext.Current.CancellationToken);

            var parakeet = second.Model.Models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);
            Assert.True(parakeet.Resumed, "The second attempt should have picked up the partial file.");
            Assert.True(parakeet.IsInstalled);
            Assert.True(second.Model.RequiredModelsReady);
        });
    }

    /// <summary>
    /// The same claim, against the real downloader rather than a fake.
    /// </summary>
    /// <remarks>
    /// The stubbed test above proves the window resumes what the provisioner tells it; this one
    /// proves the provisioner actually does. It drives <see cref="ModelDownloader"/> over a
    /// handler that fails mid-stream and then honours the <c>Range</c> header the retry sends,
    /// so the resume is the real HTTP mechanic and not a mock of one -- and no byte leaves this
    /// machine.
    /// </remarks>
    [Fact]
    public async Task TheRealDownloaderResumesFromAPartialFileWithARangeRequest()
    {
        using var jane = new TempJane();

        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var asset = new ModelAsset(
            "resume-fixture",
            new Uri("https://github.test/resume-fixture.bin"),
            sha,
            payload.Length,
            ModelArtifactKind.SingleFile,
            "resume-fixture.bin",
            "MIT",
            "Test fixture.");

        var handler = new ResumeHandler(payload, breakAfter: 20 * 1024);
        using var http = new HttpClient(handler);
        var downloader = new ModelDownloader(http, jane.Paths.Models);

        // First attempt: the stream dies part way through, and the partial file stays on disk.
        await Assert.ThrowsAnyAsync<IOException>(() =>
            downloader.EnsureAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.False(downloader.IsInstalled(asset));

        // Second attempt: it asks for the rest, and only the rest.
        handler.Break = false;
        var steps = new List<ModelDownloadProgress>();
        await downloader.EnsureAsync(
            asset,
            new Progress<ModelDownloadProgress>(steps.Add),
            TestContext.Current.CancellationToken);

        Assert.True(downloader.IsInstalled(asset));
        Assert.Equal(payload, await File.ReadAllBytesAsync(asset.ResolvePath(jane.Paths.Models), TestContext.Current.CancellationToken));

        var ranges = handler.RequestedRanges;
        Assert.Equal(2, ranges.Count);
        Assert.Null(ranges[0]);
        Assert.Equal(20 * 1024L, ranges[1]);
    }

    [Fact]
    public async Task AMicrophoneFailureIsADesignedStateRatherThanASilentZeroLevel()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        jane.MicrophoneCheck.FailWith = "The microphone is in use by another application.";

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            await window.Model.GoToAsync(OnboardingStep.Microphone, TestContext.Current.CancellationToken);

            Assert.False(window.Model.HeardYou);
            Assert.Contains("in use", window.Model.MicrophoneError!, StringComparison.OrdinalIgnoreCase);

            // A microphone Jane cannot open must not block onboarding: it can be fixed later, and
            // trapping somebody on step two of five is worse than letting them finish.
            Assert.True(window.Model.CanGoNext);
        });
    }

    [Fact]
    public async Task ARejectedHotkeyIsRefusedDuringOnboardingToo()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            await window.Model.GoToAsync(OnboardingStep.Hotkey, TestContext.Current.CancellationToken);

            Assert.False(window.Model.TryRebind(new HotkeyBinding(KeyNames.VkDelete, [KeyNames.VkControl, KeyNames.VkAlt])));
            Assert.Equal(HotkeyVerdict.Rejected, window.Model.HotkeyStatus.Verdict);
            Assert.Equal(HotkeyBinding.VkRightControl, window.Model.Hotkey.VirtualKey);

            // And the default still warns about being a game bind, which is the whole point of
            // choosing it here rather than assuming it.
            Assert.True(window.Model.TryRebind(HotkeyBinding.Default));
            Assert.Contains("push-to-talk", window.Model.HotkeyStatus.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GoingBackKeepsWhatWasAlreadyChosen()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            var model = window.Model;

            await model.GoToAsync(OnboardingStep.Microphone, TestContext.Current.CancellationToken);
            model.Microphone = model.Microphones.Single(m => m.Id == "mic-2");

            await model.GoToAsync(OnboardingStep.Hotkey, TestContext.Current.CancellationToken);
            model.Back();
            model.Back();

            Assert.Equal(OnboardingStep.Microphone, model.Step);
            Assert.Equal("mic-2", model.Microphone!.Id);
        });
    }

    [Fact]
    public async Task EveryStepHasNamedKeyboardReachableControls()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenFirstRun();
            var named = UiTree.AssertEveryInteractiveControlIsNamed(window.Chrome, "first-run chrome");
            UiTree.AssertEveryInteractiveControlIsATabStop(window.Chrome, "first-run chrome");

            foreach (var step in Enum.GetValues<OnboardingStep>())
            {
                var view = window.StepView(step);
                UiTree.Realize(view);

                named += UiTree.AssertEveryInteractiveControlIsNamed(view, $"first-run step {step}");
                UiTree.AssertEveryInteractiveControlIsATabStop(view, $"first-run step {step}");
            }

            Assert.True(named > 10, $"Only {named} interactive controls were found -- the walk is not reaching the steps.");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void EveryStepIsTitledAndExplained()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenFirstRun();

            foreach (var step in Enum.GetValues<OnboardingStep>())
            {
                var page = window.Model.Pages.Single(p => p.Step == step);

                Assert.False(string.IsNullOrWhiteSpace(page.Title), $"{step} has no title.");
                Assert.False(string.IsNullOrWhiteSpace(page.Summary), $"{step} has no explanation.");
            }
        });
    }

    [Fact]
    public void TheWelcomeStepStatesWhatJaneKeepsAndWhatItSends()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenFirstRun();
            var welcome = window.StepView(OnboardingStep.Welcome);
            UiTree.Realize(welcome);

            var text = UiTree.AllText(welcome);

            Assert.Contains("plaintext", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Audio is never", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("model weights", text, StringComparison.OrdinalIgnoreCase);
        });
    }
}

/// <summary>
/// An HTTP handler that serves a byte array, optionally cutting the stream short once, and
/// records the <c>Range</c> offset of every request it saw.
/// </summary>
/// <remarks>
/// The point of the recording is that "it resumed" is only true if the second request asked for
/// the remainder. A download that silently started over would also end up with the right file and
/// would pass a weaker assertion.
/// </remarks>
internal sealed class ResumeHandler(byte[] payload, int breakAfter) : HttpMessageHandler
{
    public bool Break { get; set; } = true;

    /// <summary>Null for a request with no Range header; otherwise the offset it asked from.</summary>
    public List<long?> RequestedRanges { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        RequestedRanges.Add(from);

        var offset = (int)(from ?? 0);
        var body = payload.AsSpan(offset).ToArray();

        HttpContent content = Break
            ? new StreamContent(new FailingStream(body, breakAfter))
            : new ByteArrayContent(body);

        return Task.FromResult(new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = content,
        });
    }
}

/// <summary>A stream that hands back some bytes and then faults, as a dropped connection does.</summary>
internal sealed class FailingStream(byte[] payload, int breakAfter) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => payload.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= breakAfter)
        {
            throw new IOException("The connection was closed by the remote host.");
        }

        var take = Math.Min(buffer.Length, Math.Min(breakAfter, payload.Length) - _position);
        payload.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A microphone check that reports a healthy level, or a failure when told to.</summary>
internal sealed class FakeMicrophoneCheck : IMicrophoneCheck
{
    /// <summary>Set to make every start report this failure instead of a level.</summary>
    public string? FailWith { get; set; }

    public List<string?> Started { get; } = [];

    public IDisposable Start(string? deviceId, Action<float> onLevel, Action<string> onFailure)
    {
        Started.Add(deviceId);

        if (FailWith is not null)
        {
            onFailure(FailWith);
            return new Stub();
        }

        // Loud enough to clear the "did Jane hear you" threshold, which is what a real voice does
        // within a second or two of the meter starting.
        onLevel(0.02f);
        onLevel(0.41f);
        return new Stub();
    }

    private sealed class Stub : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
