using System.IO;
using Jane.App.Composition;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Models;
using Jane.Core.Pipeline;
using Jane.Core.Platform;
using Jane.Speech;

namespace Jane.App.Tests;

/// <summary>
/// Jane on a machine it has just been installed on: no speech model, no voice detection model, no
/// language-model runtime.
/// </summary>
/// <remarks>
/// <para>
/// This is the state every new user starts in and the one state the developer's machine was never
/// in. The first release failed in it completely. Building the dictation graph threw on the
/// missing voice detection model, and because the settings window, the history window and
/// onboarding were all reached <em>through</em> that graph, none of them could open: the tray
/// icon appeared, said the model was missing and to open settings, and clicking Settings did
/// nothing at all.
/// </para>
/// <para>
/// Three separate things had to be true for first run to work, and none of them was:
/// </para>
/// <list type="number">
/// <item><description>The graph composes with nothing downloaded.</description></item>
/// <item><description>A model downloaded while Jane is running is picked up without a restart.</description></item>
/// <item><description>A language-model runtime installed while Jane is running is too.</description></item>
/// </list>
/// </remarks>
public sealed class FirstRunTests
{
    [Fact]
    public async Task JaneComposesOnAMachineWithNothingDownloaded()
    {
        using var sta = new StaTestContext();
        using var home = new TempHome();

        JaneHost? host = null;
        var failure = sta.Invoke(() => Record.Exception(() =>
            host = JaneHost.Create(sta.Dispatcher, new SilentOverlay(), home.Paths)));

        try
        {
            Assert.Null(failure);
            Assert.NotNull(host);

            // Everything the settings window and onboarding are built from.
            Assert.NotNull(host!.Settings2);
            Assert.NotNull(host.Provisioner);
            Assert.NotNull(host.Dictionary);
            Assert.NotNull(host.History);
            Assert.False(host.Provisioner.IsInstalled(ModelCatalog.SileroVad));
            Assert.False(host.Provisioner.IsInstalled(ModelCatalog.ParakeetV2Int8));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public void VoiceDetectionIsBuiltWhenItIsFirstNeededNotWhenJaneStarts()
    {
        var built = 0;
        using var gate = new SileroVoiceActivityGate(() =>
        {
            built++;
            throw new VadModelMissingException(@"C:\nowhere\silero_vad.onnx");
        });

        // Constructing it asks for nothing. That is the whole fix for composition.
        Assert.Equal(0, built);

        var failure = Assert.Throws<SpeechEngineUnavailableException>(() => gate.Process(new float[1600]));

        // Said in words somebody can act on, and typed so the pipeline treats it as "not
        // downloaded yet" rather than as a crash.
        Assert.Contains("voice detection", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settings", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<VadModelMissingException>(failure.InnerException);
    }

    [Fact]
    public void AVoiceDetectionModelThatWasMissingIsLookedForAgain()
    {
        var attempts = 0;
        using var gate = new SileroVoiceActivityGate(() =>
        {
            attempts++;
            throw new VadModelMissingException(@"C:\nowhere\silero_vad.onnx");
        });

        Assert.Throws<SpeechEngineUnavailableException>(() => gate.Process(new float[1600]));
        Assert.Throws<SpeechEngineUnavailableException>(() => gate.Process(new float[1600]));

        // Not remembered. The download finishing in between is the case this exists for.
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DictationIsPassedThroughUntouchedUntilALanguageModelIsBound()
    {
        var formatter = new SwitchableFormatter();

        Assert.False(formatter.IsBound);
        Assert.Equal(
            "send it tuesday",
            await formatter.FormatAsync("send it tuesday", FormattingContext.Empty, TestContext.Current.CancellationToken));

        formatter.Bind(new ShoutingFormatter());

        // The orchestrator holds this one object for its whole life. Binding changes what it does
        // without the orchestrator being rebuilt, which is what "no restart" means here.
        Assert.True(formatter.IsBound);
        Assert.Equal(
            "SEND IT TUESDAY",
            await formatter.FormatAsync("send it tuesday", FormattingContext.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARewriteIsRefusedUntilALanguageModelIsBound()
    {
        var rewriter = new SwitchableRewriter();

        Assert.Equal(
            string.Empty,
            await rewriter.RewriteAsync("some text", "make it shorter", FormattingContext.Empty, TestContext.Current.CancellationToken));

        rewriter.Bind(new FixedRewriter("shorter"));

        Assert.Equal(
            "shorter",
            await rewriter.RewriteAsync("some text", "make it shorter", FormattingContext.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithNoRuntimeALanguageModelCannotBePulledAndSaysWhy()
    {
        var weights = new FakeWeights();
        var provisioner = new LateBoundProvisioner(weights, _ => Task.FromResult<LanguageModelAccess?>(null), () => { });

        Assert.False(await provisioner.IsPulledAsync("qwen3:4b-instruct", TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            provisioner.PullAsync("qwen3:4b-instruct", null, TestContext.Current.CancellationToken));

        Assert.Contains("no model runtime", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallingTheRuntimeMakesTheLanguageModelsDownloadableStraightAway()
    {
        // The freshly installed case, in one test. The runtime is not there at launch, so no
        // language-model server exists. The user installs it from the wizard -- and the very next
        // row, the model itself, must work without Jane being restarted first.
        var weights = new FakeWeights();
        var pulled = new List<string>();
        var bringUps = 0;

        var provisioner = new LateBoundProvisioner(
            weights,
            _ =>
            {
                bringUps++;
                return Task.FromResult(weights.HostInstalled
                    ? new LanguageModelAccess(
                        (model, _, _) =>
                        {
                            pulled.Add(model);
                            return Task.CompletedTask;
                        },
                        (model, _) => Task.FromResult(pulled.Contains(model)))
                    : null);
            },
            () => { });

        await provisioner.InstallHostAsync(null, TestContext.Current.CancellationToken);

        // Brought up as part of the install, not left for whoever asks next.
        Assert.True(bringUps >= 1);

        await provisioner.PullAsync("qwen3:4b-instruct", null, TestContext.Current.CancellationToken);

        Assert.Equal(["qwen3:4b-instruct"], pulled);
        Assert.True(await provisioner.IsPulledAsync("qwen3:4b-instruct", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadingAModelTellsThePipelineItHasArrived()
    {
        var weights = new FakeWeights();
        var announced = 0;
        var provisioner = new LateBoundProvisioner(
            weights, _ => Task.FromResult<LanguageModelAccess?>(null), () => announced++);

        await provisioner.EnsureAsync(ModelCatalog.ParakeetV2Int8, null, TestContext.Current.CancellationToken);

        // So the engine can start loading while the user is still on the wizard's next page,
        // rather than on the first key press.
        Assert.Equal(1, announced);
        Assert.True(provisioner.IsInstalled(ModelCatalog.ParakeetV2Int8));
    }

    [Fact]
    public async Task ADownloadThatFailedAnnouncesNothing()
    {
        var weights = new FakeWeights { Fails = true };
        var announced = 0;
        var provisioner = new LateBoundProvisioner(
            weights, _ => Task.FromResult<LanguageModelAccess?>(null), () => announced++);

        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            provisioner.EnsureAsync(ModelCatalog.ParakeetV2Int8, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, announced);
    }

    [Theory]
    [InlineData(typeof(VadModelMissingException))]
    [InlineData(typeof(InvalidOperationException))]
    public void AFailedStartIsDescribedInWordsNotExceptionNames(Type kind)
    {
        Exception failure = kind == typeof(VadModelMissingException)
            ? new VadModelMissingException(@"C:\nowhere\silero_vad.onnx")
            : new InvalidOperationException("boom");

        var message = StartupFailure.Describe(failure);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMenuItemThatCannotOpenItsWindowSaysSoInsteadOfDoingNothing()
    {
        // The report that found all of this: "I would click on it and nothing would happen."
        // Whatever stops a window opening, a click must produce something.
        var message = StartupFailure.WindowUnavailable(startupFailure: null);
        Assert.Contains("starting", message, StringComparison.OrdinalIgnoreCase);

        var afterFailure = StartupFailure.WindowUnavailable("Jane could not start its dictation pipeline.");
        Assert.Contains("could not start", afterFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("log", afterFailure, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempHome : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jane-first-run-" + Guid.NewGuid().ToString("N"));

        public TempHome()
        {
            Directory.CreateDirectory(_root);

            // Both given explicitly, so neither JANE_HOME nor JANE_MODEL_DIR on the machine
            // running the tests can point this at a directory that has models in it.
            Paths = new JanePaths(_root, Path.Combine(_root, "models"));
        }

        public JanePaths Paths { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can hold the file a moment longer than the connection. A temp directory
                // left behind is not worth failing a test over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class SilentOverlay : IOverlayPresenter
    {
        public OverlayStatus Status { get; private set; } = OverlayStatus.Idle;

        public void Show(OverlayStatus status) => Status = status;

        public void Hide()
        {
        }
    }

    private sealed class ShoutingFormatter : ITranscriptFormatter
    {
        public Task<string> FormatAsync(string transcript, FormattingContext context, CancellationToken cancellationToken) =>
            Task.FromResult(transcript.ToUpperInvariant());
    }

    private sealed class FixedRewriter(string result) : ISelectionRewriter
    {
        public Task<string> RewriteAsync(
            string selection, string instruction, FormattingContext context, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    /// <summary>The weights-and-runtime half of provisioning, with no network behind it.</summary>
    private sealed class FakeWeights : IModelProvisioner
    {
        private readonly HashSet<string> _installed = [];

        public bool Fails { get; init; }

        public bool HostInstalled { get; private set; }

        public bool IsInstalled(ModelAsset asset) => _installed.Contains(asset.Id);

        public Task EnsureAsync(ModelAsset asset, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (Fails)
            {
                return Task.FromException(new ModelDownloadException(ModelDownloadFailure.Offline, "No connection."));
            }

            _installed.Add(asset.Id);
            return Task.CompletedTask;
        }

        public Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromException(new ModelDownloadException(
                ModelDownloadFailure.ServiceUnreachable,
                $"There is no model runtime on this machine, so {model} cannot be pulled."));

        public ModelHostState Host => HostInstalled
            ? ModelHostState.Ready("Installed.")
            : ModelHostState.Missing("Not installed.", "Install it.");

        public Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            HostInstalled = true;
            return Task.CompletedTask;
        }
    }
}
