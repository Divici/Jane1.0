using System.Collections;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Jane.App.Controls;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.History;
using Jane.Core.Instructions;
using Jane.Core.Models;
using Jane.Core.Platform;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Core.Text;
using Jane.Core.Vocabulary;
using Jane.Speech;

namespace Jane.App.Tests;

/// <summary>
/// The settings window is where every promise the plan makes becomes a control somebody can
/// actually reach. These tests assert the two things a screenshot cannot: that a change lands in
/// SQLite rather than in a field, and that everything on screen has a name and a tab stop.
/// </summary>
public sealed class SettingsWindowTests
{
    [Fact]
    public void ThemeLoadsWithoutAnApplicationObject()
    {
        // The tests drive real windows on a bare dispatcher: an Application is process-wide
        // singleton state that one test would be imposing on every other. A theme that only
        // resolves through Application.Current would work in the app and fail here -- and the
        // failure would look like a broken test rather than a broken window.
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var element = new Grid();
            JaneTheme.ApplyTo(element);

            Assert.NotNull(element.TryFindResource("JaneButton"));
            Assert.NotNull(element.TryFindResource("JaneWindow"));
        });
    }

    [Fact]
    public async Task ChangingOverlayVisibilityReachesTheStoreAndTheOverlay()
    {
        // The window's job stops at the write. Getting the new value to the pill is
        // SettingsRepository.Changed's job, and LiveSettingsTests is where that is pinned -- the
        // callback this test used to assert on was a second, redundant path to the same place,
        // racing the write it duplicated.
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        var applied = new List<OverlaySettings>();
        jane.Settings.Changed += (_, settings) => applied.Add(settings.Overlay);

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();

            Assert.True(window.Model.OverlayVisible);

            window.Model.OverlayVisible = false;
            await window.Model.LastWrite;
        });

        // Read through a second repository over the same file: the cached value proves nothing.
        Assert.False(jane.ReopenSettings().Overlay.Visible);
        Assert.False(Assert.Single(applied).Visible);
    }

    [Fact]
    public async Task HowNumbersAndSymbolsAreWrittenIsAChoiceOnScreen()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var model = window.Model;

            // Both start on, because a number arriving as a word was the complaint.
            Assert.Equal(NumberStyle.DigitsExceptLoneOne, model.Numbers);
            Assert.True(model.SpokenSymbols);

            var formatting = window.SectionView(SettingsSection.Formatting);
            UiTree.Realize(formatting);

            var numbers = UiTree.ById<ComboBox>(formatting, "NumberStyle");
            var symbols = UiTree.ById<CheckBox>(formatting, "SpokenSymbols");
            Assert.Equal(3, numbers.Items.Count);
            Assert.True(symbols.IsChecked);

            model.NumberStyleChoiceValue = model.NumberStyleChoices.Single(c => c.Style == NumberStyle.AsSpoken);
            model.SpokenSymbols = false;
            await model.LastWrite;
        });

        var stored = jane.ReopenSettings();

        Assert.Equal(NumberStyle.AsSpoken, stored.Text.Numbers);
        Assert.False(stored.Text.SpokenSymbols);
    }

    [Fact]
    public async Task EverySettingRoundTripsThroughSqlite()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var model = window.Model;

            model.Mode = HotkeyMode.Toggle;
            model.MinimumHoldMs = 450;
            model.MaxToggleSeconds = 120;
            model.Microphone = model.Microphones.Single(m => m.Id == "mic-2");
            model.Engine = model.Engines.First(e => e.Id.StartsWith("whisper", StringComparison.Ordinal));
            model.NumThreads = 6;
            model.LlmEnabled = false;
            model.InGame = InGameBehaviour.UseCpuLlm;
            model.IdleUnloadSeconds = 90;
            model.BusyUtilisationPercent = 55;
            model.MinimumFreeVramGb = 6;
            model.OverlayShowContextIndicator = false;

            await model.LastWrite;
        });

        var stored = jane.ReopenSettings();

        Assert.Equal(HotkeyMode.Toggle, stored.Hotkey.Mode);
        Assert.Equal(450, stored.Hotkey.MinimumHoldMs);
        Assert.Equal(120_000, stored.Hotkey.MaxToggleDurationMs);
        Assert.Equal("mic-2", stored.MicrophoneDeviceId);
        Assert.StartsWith("whisper", stored.Speech.EngineId, StringComparison.Ordinal);
        Assert.Equal(6, stored.Speech.NumThreads);
        Assert.False(stored.Llm.Enabled);
        Assert.Equal(InGameBehaviour.UseCpuLlm, stored.Llm.InGame);
        Assert.Equal(90, stored.Llm.IdleUnloadSeconds);
        Assert.Equal(55, stored.Gpu.BusyUtilisationPercent);
        Assert.Equal(6L * 1024 * 1024 * 1024, stored.Gpu.MinimumFreeVramBytes);
        Assert.False(stored.Overlay.ShowContextIndicator);
    }

    [Theory]
    [InlineData(KeyNames.VkDelete, new[] { KeyNames.VkControl, KeyNames.VkAlt }, "Secure Attention")]
    [InlineData(KeyNames.VkEscape, new[] { KeyNames.VkControl, KeyNames.VkShift }, "Task Manager")]
    [InlineData(KeyNames.VkTab, new[] { KeyNames.VkAlt }, "window switcher")]
    [InlineData(0x4C, new[] { KeyNames.VkLeftWindows }, "Windows key")]
    [InlineData(0x41, new int[0], "types a character")]
    [InlineData(KeyNames.VkEscape, new int[0], "already Jane's")]
    public void RebindingRejectsAConflictingSystemCombination(int key, int[] modifiers, string because)
    {
        var verdict = HotkeyValidator.Validate(new HotkeyBinding(key, modifiers));

        Assert.Equal(HotkeyVerdict.Rejected, verdict.Verdict);
        Assert.Contains(because, verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARejectedRebindDoesNotReachTheStore()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var model = window.Model;

            var accepted = model.TryRebind(new HotkeyBinding(KeyNames.VkDelete, [KeyNames.VkControl, KeyNames.VkAlt]));

            Assert.False(accepted);
            Assert.Equal(HotkeyVerdict.Rejected, model.HotkeyStatus.Verdict);
            Assert.Equal(HotkeyBinding.VkRightControl, model.Hotkey.VirtualKey);

            await model.LastWrite;
        });

        Assert.Equal(HotkeyBinding.VkRightControl, jane.ReopenSettings().Hotkey.VirtualKey);
    }

    [Fact]
    public void RightControlIsAcceptedButWarnsThatItIsACommonGameBind()
    {
        var verdict = HotkeyValidator.Validate(HotkeyBinding.Default);

        Assert.Equal(HotkeyVerdict.Warned, verdict.Verdict);
        Assert.True(verdict.IsAcceptable);
        Assert.Contains("push-to-talk", verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("game", verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAcceptedRebindReachesTheStore()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();

            // F13: no printable character, no shell claim, on no game's default bind list.
            Assert.True(window.Model.TryRebind(new HotkeyBinding(0x7C, [])));
            await window.Model.LastWrite;
        });

        Assert.Equal(0x7C, jane.ReopenSettings().Hotkey.VirtualKey);
    }

    [Fact]
    public async Task DictionaryEditorAddsEditsAndRemovesTerms()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var editor = window.Model.Dictionary;

            editor.DraftTerm = "Kubernetes";
            editor.DraftPronunciation = "koober netties";
            editor.DraftReplacement = "Kubernetes";
            await editor.AddAsync(TestContext.Current.CancellationToken);

            Assert.Empty(editor.DraftTerm);
            var entry = Assert.Single(editor.Entries);
            Assert.Equal("Kubernetes", entry.Term);

            await editor.RemoveAsync(entry, TestContext.Current.CancellationToken);
            Assert.Empty(editor.Entries);
        });

        Assert.Empty(jane.Dictionary.Entries);
    }

    [Fact]
    public async Task InstructionsEditorSavesGlobalAndPerAppRules()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var editor = window.Model.Instructions;

            editor.GlobalText = "Never use exclamation marks.";
            await editor.SaveGlobalAsync(TestContext.Current.CancellationToken);

            editor.DraftProcess = "Code.exe";
            editor.DraftText = "Prefer lower case and no trailing full stop.";
            await editor.AddAppRuleAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal("Never use exclamation marks.", jane.Instructions.Global);

        // "Code.exe" is normalised to the form the focus tracker reports.
        var resolved = jane.Instructions.ResolveFor("code");
        Assert.Contains("lower case", resolved.PerApp);
    }

    [Fact]
    public async Task ModelManagementShowsSizeLicenceAndInstallStateAndDownloadsOnDemand()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var models = window.Model.Models;

            var parakeet = models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);
            Assert.Equal("CC-BY-4.0", parakeet.License);
            Assert.Equal("460 MB", parakeet.SizeDescription);
            Assert.False(parakeet.IsInstalled);

            await parakeet.Download.ExecuteAsync(null);

            Assert.True(parakeet.IsInstalled);
            Assert.Equal(1.0, parakeet.Progress, 3);
            Assert.Null(parakeet.Error);
        });
    }

    [Fact]
    public async Task AFailedDownloadShowsTheRemedyAndOffersRetry()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();
        jane.Provisioner.FailWith = new ModelDownloadException(
            ModelDownloadFailure.Offline, "Could not reach github.com.");

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenSettings();
            var parakeet = window.Model.Models.Single(m => m.Id == ModelCatalog.ParakeetV2Int8.Id);

            await parakeet.Download.ExecuteAsync(null);

            Assert.False(parakeet.IsInstalled);
            Assert.NotNull(parakeet.Error);
            Assert.Contains("network connection", parakeet.Error!.Remedy, StringComparison.OrdinalIgnoreCase);
            Assert.True(parakeet.CanRetry);

            jane.Provisioner.FailWith = null;
            await parakeet.Download.ExecuteAsync(null);

            Assert.True(parakeet.IsInstalled);
            Assert.Null(parakeet.Error);
        });
    }

    [Fact]
    public void DeepContextSectionShowsTheBlocklistAndAnswersWhetherAWindowWouldBeRead()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenSettings();
            var inspector = window.Model.DeepContext;

            Assert.NotEmpty(inspector.Processes);
            Assert.NotEmpty(inspector.TitleFragments);
            Assert.NotEmpty(inspector.BrowserTitleFragments);

            inspector.ProbeProcess = "chrome";
            inspector.ProbeTitle = "Chase - Account summary";
            inspector.Probe();

            Assert.True(inspector.ProbeResult!.IsBlocked);
            Assert.Contains("would not be read", inspector.ProbeMessage, StringComparison.OrdinalIgnoreCase);

            inspector.ProbeTitle = "Jane - plan.md";
            inspector.Probe();

            Assert.False(inspector.ProbeResult!.IsBlocked);
        });
    }

    [Fact]
    public void AboutAttributesEveryModelAndRuntimeWithoutContradictingNotice()
    {
        var notice = File.ReadAllText(PackagingTests.FindRepoFile("NOTICE.md"));

        foreach (var entry in Attributions.All)
        {
            Assert.Contains(entry.Component, notice, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(entry.License, notice, StringComparison.OrdinalIgnoreCase);
        }

        // The four the plan names explicitly, with the licence it names for each.
        Assert.Contains(Attributions.All, a => a.Component.Contains("Parakeet", StringComparison.OrdinalIgnoreCase) && a.License == "CC-BY-4.0");
        Assert.Contains(Attributions.All, a => a.Component.Contains("Qwen3", StringComparison.OrdinalIgnoreCase) && a.License.Contains("Apache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Attributions.All, a => a.Component.Contains("Whisper", StringComparison.OrdinalIgnoreCase) && a.License == "MIT");
        Assert.Contains(Attributions.All, a => a.Component.Contains("sherpa-onnx", StringComparison.OrdinalIgnoreCase) && a.License.Contains("Apache", StringComparison.OrdinalIgnoreCase));

        // CC-BY requires the creator to be named, not merely the licence.
        var parakeet = Attributions.All.Single(a => a.Component.Contains("Parakeet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("NVIDIA", parakeet.Holder, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutViewRendersEveryAttribution()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenSettings();
            var about = window.SectionView(SettingsSection.About);
            UiTree.Realize(about);

            var text = string.Concat(UiTree.Descendants<TextBlock>(about).Select(t => t.Text + "\n"));

            foreach (var entry in Attributions.All)
            {
                Assert.Contains(entry.Component, text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(entry.License, text, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void EveryInteractiveControlInEverySectionHasAnAccessibleName()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenSettings();
            var named = UiTree.AssertEveryInteractiveControlIsNamed(window.Chrome, "settings window chrome");

            foreach (var section in Enum.GetValues<SettingsSection>())
            {
                var view = window.SectionView(section);
                UiTree.Realize(view);
                named += UiTree.AssertEveryInteractiveControlIsNamed(view, $"settings section {section}");
            }

            // A guard on the walk itself. Every one of these assertions passes vacuously if the
            // layout pass stops realising controls, and the failure would look like a pass.
            Assert.True(named > 25, $"Only {named} interactive controls were found -- the walk is not reaching the panes.");
        });
    }

    [Fact]
    public void EveryInteractiveControlInEverySectionIsReachableByTab()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenSettings();
            UiTree.AssertEveryInteractiveControlIsATabStop(window.Chrome, "settings window chrome");

            foreach (var section in Enum.GetValues<SettingsSection>())
            {
                var view = window.SectionView(section);
                UiTree.Realize(view);
                UiTree.AssertEveryInteractiveControlIsATabStop(view, $"settings section {section}");
            }
        });
    }

    [Fact]
    public void EverySectionIsNamedAndReachableFromTheNavigation()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenSettings();

            foreach (var section in Enum.GetValues<SettingsSection>())
            {
                window.Show(section);
                Assert.Equal(section, window.CurrentSection);
                Assert.False(string.IsNullOrWhiteSpace(window.TitleOf(section)));
            }
        });
    }
}

/// <summary>
/// A whole Jane data layer in a temp directory, torn down with the test.
/// </summary>
/// <remarks>
/// Every one of these tests opens a real SQLite database and asserts through it, because the
/// thing worth proving about a settings window is that the value left the window. What none of
/// them may do is touch <c>%LOCALAPPDATA%\Jane</c>: <see cref="JanePaths"/> takes a root for
/// exactly this reason.
/// </remarks>
internal sealed class TempJane : IDisposable
{
    public TempJane()
    {
        Root = Path.Combine(Path.GetTempPath(), "jane-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Paths = new JanePaths(Root, Path.Combine(Root, "models"));
        Database = JaneDatabase.Open(Paths);
        Settings = new SettingsRepository(Database);
        Dictionary = new UserDictionary(Database);
        Instructions = new CustomInstructions(Database);
        History = new HistoryStore(Database);
        Provisioner = new FakeProvisioner();
        Microphones = new FakeMicrophones();
        MicrophoneCheck = new FakeMicrophoneCheck();
    }

    public string Root { get; }

    public JanePaths Paths { get; }

    public JaneDatabase Database { get; }

    public SettingsRepository Settings { get; }

    public UserDictionary Dictionary { get; }

    public CustomInstructions Instructions { get; }

    public HistoryStore History { get; }

    public FakeProvisioner Provisioner { get; }

    public FakeMicrophones Microphones { get; }

    public FakeMicrophoneCheck MicrophoneCheck { get; }

    public Jane.App.Onboarding.FirstRunWindow OpenFirstRun(
        Func<CancellationToken, Task<string?>>? dictation = null) =>
        new(Settings,
            Provisioner,
            Microphones,
            MicrophoneCheck,
            dictation ?? (_ => Task.FromResult<string?>(null)));

    public Jane.App.Settings.SettingsWindow OpenSettings() =>
        new(Settings, Dictionary, Instructions, Provisioner, Microphones, new Blocklist(), Database);

    /// <param name="live">What the focus tracker reports right now. Defaults to nothing focused.</param>
    /// <param name="inject">Stands in for the real injector, which these tests do not have.</param>
    public Jane.App.History.HistoryWindow OpenHistory(
        Func<TargetWindow>? live = null,
        Action<HistoryEntry>? inject = null,
        Action<string>? copy = null) =>
        new(History,
            live ?? (() => TargetWindow.None),
            inject ?? (_ => { }),
            copy ?? (_ => { }),
            Database);

    /// <summary>Writes a few dictations, oldest first, so ordering is deterministic.</summary>
    public async Task SeedHistoryAsync(params (string Text, TargetWindow Target)[] entries)
    {
        var when = DateTimeOffset.Now.AddMinutes(-entries.Length);

        foreach (var (text, target) in entries)
        {
            when = when.AddMinutes(1);

            await History.AppendAsync(
                new HistoryEntry
                {
                    CreatedAt = when,
                    RawTranscript = text.ToLowerInvariant(),
                    FinalText = text,
                    Target = target,
                    EngineId = "parakeet-tdt-0.6b-v2-int8",
                    LlmModel = "jane-qwen3-4b",
                    BypassReason = "formatted",
                    Injected = true,
                    Timings = new StageTimings(
                        TimeSpan.FromMilliseconds(20),
                        TimeSpan.FromMilliseconds(15),
                        TimeSpan.FromMilliseconds(180),
                        TimeSpan.FromMilliseconds(40),
                        TimeSpan.FromMilliseconds(620),
                        TimeSpan.FromMilliseconds(30),
                        TimeSpan.FromMilliseconds(905)),
                },
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Reads settings back through a fresh repository, so no cache can flatter the test.</summary>
    public JaneSettings ReopenSettings() => new SettingsRepository(Database).Read();

    public void Dispose()
    {
        Database.Dispose();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is untidy, not a failure.
        }
    }
}

/// <summary>A provisioner that installs instantly, or fails on demand with a real typed error.</summary>
internal sealed class FakeProvisioner : IModelProvisioner
{
    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pulled = new(StringComparer.Ordinal);

    /// <summary>Set to make the next call throw. Cleared by the caller to simulate a retry.</summary>
    public ModelDownloadException? FailWith { get; set; }

    /// <summary>
    /// Set to fail only the language-model pulls, leaving the speech weights working.
    /// </summary>
    /// <remarks>
    /// This is the shape of a real machine with no Ollama runtime installed: the two required
    /// models download over HTTPS like anything else, and every pull fails because there is no
    /// server to pull into.
    /// </remarks>
    public Exception? FailPullsWith { get; set; }

    /// <summary>Bytes reported as already on disk, so a resumed download can be asserted.</summary>
    public Dictionary<string, long> AlreadyDownloaded { get; } = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>What the runtime probe reports. Defaults to a machine that has one.</summary>
    public ModelHostState Host { get; set; } = ModelHostState.Ready("Model runtime present.");

    /// <summary>Set to make the presence probe throw, as an unreachable server does.</summary>
    public Exception? PresenceThrows { get; set; }

    public Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("install-host");
        progress?.Report(new ModelDownloadProgress("ollama-runtime", 1, 1, "installed"));
        Host = ModelHostState.Ready("Model runtime present.");
        return Task.CompletedTask;
    }

    public bool IsInstalled(ModelAsset asset) => _installed.Contains(asset.Id);

    public async Task EnsureAsync(
        ModelAsset asset,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls.Add("ensure:" + asset.Id);

        var resumeFrom = AlreadyDownloaded.GetValueOrDefault(asset.Id);
        if (resumeFrom > 0)
        {
            progress?.Report(new ModelDownloadProgress(asset.Id, resumeFrom, asset.SizeBytes, "resuming"));
        }

        await Task.Yield();

        if (FailWith is not null)
        {
            AlreadyDownloaded[asset.Id] = asset.SizeBytes / 2;
            throw FailWith;
        }

        progress?.Report(new ModelDownloadProgress(asset.Id, asset.SizeBytes, asset.SizeBytes, "installing"));
        AlreadyDownloaded.Remove(asset.Id);
        _installed.Add(asset.Id);
    }

    public Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken) =>
        PresenceThrows is { } ex
            ? Task.FromException<bool>(ex)
            : Task.FromResult(_pulled.Contains(model));

    public async Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("pull:" + model);
        await Task.Yield();

        if (FailPullsWith is not null)
        {
            throw FailPullsWith;
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        progress?.Report(new LlmPullProgress(model, 100, 100, "writing manifest"));
        _pulled.Add(model);
    }
}

/// <summary>Two microphones and a default, so device selection is assertable without hardware.</summary>
internal sealed class FakeMicrophones : IMicrophoneCatalog
{
    public List<MicrophoneChoice> Devices { get; } =
    [
        new(null, "Windows default (Communications)", IsDefault: true),
        new("mic-1", "Headset Microphone (USB Audio Device)"),
        new("mic-2", "Microphone Array (Realtek)"),
    ];

    public IReadOnlyList<MicrophoneChoice> List() => Devices;
}

/// <summary>Visual-tree helpers shared by all three window test classes.</summary>
internal static class UiTree
{
    /// <summary>
    /// Types a person operates. Anything focusable that is not one of these is chrome.
    /// </summary>
    private static readonly Type[] Interactive =
    [
        typeof(ButtonBase), typeof(TextBoxBase), typeof(Selector), typeof(Slider),
        typeof(TabItem), typeof(MenuItem), typeof(PasswordBox),
    ];

    /// <summary>
    /// Runs a layout pass over a detached element so its templates and item containers exist.
    /// </summary>
    /// <remarks>
    /// Without this, a walk over an unshown window finds the nav rail and nothing else: WPF
    /// realises a <c>ContentPresenter</c>'s children during measure, and a window that has never
    /// been shown has never measured. Doing it detached rather than by showing the window keeps
    /// these tests from stealing focus from whatever else is running.
    /// </remarks>
    public static void Realize(FrameworkElement element)
    {
        element.Measure(new Size(1180, 4000));
        element.Arrange(new Rect(0, 0, 1180, 4000));
        element.UpdateLayout();
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject => Descendants(root).OfType<T>();

    /// <summary>Finds a control by the automation id its XAML declares.</summary>
    public static T ById<T>(DependencyObject root, string automationId)
        where T : DependencyObject =>
        Descendants<T>(root).FirstOrDefault(e =>
            string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(e), automationId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No {typeof(T).Name} with AutomationId '{automationId}'.");

    private static IEnumerable<FrameworkElement> InteractiveIn(DependencyObject root) =>
        Descendants<FrameworkElement>(root)
            .Where(e => Interactive.Any(t => t.IsInstanceOfType(e)))
            .Where(e => e.IsVisible || e.Visibility == Visibility.Visible)
            .Where(e => e is not ToggleButton { Name: "PART_Toggle" })

            // Repeat buttons inside a scroll bar or a slider track are template plumbing: they
            // are not tab stops and a screen reader never lands on them.
            .Where(e => e is not RepeatButton);

    public static int AssertEveryInteractiveControlIsNamed(DependencyObject root, string where)
    {
        var checkedControls = 0;

        foreach (var element in InteractiveIn(root))
        {
            checkedControls++;

            var explicitName = System.Windows.Automation.AutomationProperties.GetName(element);
            var peerName = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName();
            var name = string.IsNullOrWhiteSpace(explicitName) ? peerName : explicitName;

            Assert.False(
                string.IsNullOrWhiteSpace(name),
                $"{Describe(element)} in {where} has no accessible name. A screen reader would announce only its type.");
        }

        // Returned rather than asserted here: a pane can legitimately have nothing to operate --
        // About is a reading surface -- so the caller asserts the total across the window, which
        // is what actually catches a walk that has silently stopped finding anything.
        return checkedControls;
    }

    public static void AssertEveryInteractiveControlIsATabStop(DependencyObject root, string where)
    {
        foreach (var element in InteractiveIn(root))
        {
            // Items inside a list or a nav rail are reached with the arrow keys once the list
            // itself has focus, which is the platform convention rather than a gap.
            if (element is ListBoxItem or TabItem or MenuItem)
            {
                continue;
            }

            if (element is Control { IsEnabled: false })
            {
                continue;
            }

            var control = element as Control;
            Assert.True(
                control is null || (control.Focusable && control.IsTabStop),
                $"{Describe(element)} in {where} cannot be reached with Tab.");
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var id = System.Windows.Automation.AutomationProperties.GetAutomationId(element);
        var content = (element as ContentControl)?.Content as string;
        var label = id.Length > 0 ? id : content ?? element.Name;

        return string.IsNullOrEmpty(label)
            ? element.GetType().Name
            : $"{element.GetType().Name} '{label}'";
    }

    /// <summary>Every string a pane puts on screen, for asserting that it says something plainly.</summary>
    public static string AllText(DependencyObject root)
    {
        var blocks = Descendants<TextBlock>(root).Select(t => t.Text);
        var runs = Descendants<TextBlock>(root)
            .SelectMany(t => t.Inlines.OfType<Run>())
            .Select(r => r.Text);

        return string.Join("\n", blocks.Concat(runs).Where(t => !string.IsNullOrWhiteSpace(t)));
    }
}

/// <summary>Dispatcher helpers for tests whose body is asynchronous.</summary>
internal static class StaAsync
{
    /// <summary>
    /// Runs an asynchronous body on the STA thread and waits for it.
    /// </summary>
    /// <remarks>
    /// <c>Dispatcher.Invoke(Func&lt;Task&gt;)</c> returns as soon as the first await yields, which
    /// would have every one of these tests assert against work that has not happened. This awaits
    /// the returned task instead, on the dispatcher, so continuations run on the STA thread the
    /// windows live on.
    /// </remarks>
    public static Task InvokeAsync(this StaTestContext sta, Func<Task> body) =>
        sta.Dispatcher.InvokeAsync(body).Task.Unwrap();
}
