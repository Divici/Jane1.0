using System.Text.Json;
using Jane.Core.Abstractions;
using Jane.Core.Platform;
using Jane.Core.Settings;

namespace Jane.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-settings-test");

    private JanePaths Paths => new(_root.FullName, Path.Combine(_root.FullName, "models"));

    [Fact]
    public void MissingFile_YieldsDefaultsRatherThanThrowing()
    {
        using var store = new SettingsStore(Paths);

        var settings = store.Read();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal("parakeet-tdt-0.6b-v2-int8", settings.Speech.EngineId);
        Assert.Equal(HotkeyBinding.VkRightControl, settings.Hotkey.VirtualKey);
        Assert.Equal(HotkeyMode.Hold, settings.Hotkey.Mode);
        Assert.False(settings.OnboardingComplete);
    }

    [Fact]
    public void DefaultsEncodeThePlansLockedBehaviour()
    {
        var settings = new JaneSettings();

        // In-game default is LLM off, injecting raw Parakeet output. Routing to a CPU model
        // instead is opt-in, because an eight-thread CPU prefill burst hurts a running game more
        // than the GPU call it was meant to avoid.
        Assert.Equal(InGameBehaviour.SkipLlm, settings.Llm.InGame);

        // Never keep_alive 0 per request -- that would re-pay the cold load on every dictation.
        Assert.Equal("180s", settings.Llm.KeepAlive);
        Assert.True(settings.Llm.IdleUnloadSeconds > 0);

        // Right Ctrl is a common game bind, so the hotkey is off during games by default and a
        // stray tap under 300 ms costs nothing.
        Assert.False(settings.Hotkey.EnabledInGame);
        Assert.Equal(300, settings.Hotkey.MinimumHoldMs);

        // Biasing forces beam search, so it stays off until `bench` proves it affordable.
        Assert.False(settings.Speech.EnableHotwordBiasing);
    }

    [Fact]
    public async Task BenchWinnerVisibleToAppOnReload()
    {
        // `bench` and the app are separate processes sharing one file. If the app could not see
        // a fresh selection without a restart, "the machine picks its own engine" would not hold.
        using (var bench = new SettingsStore(Paths))
        {
            await bench.UpdateAsync(s => s with
            {
                Speech = s.Speech with
                {
                    EngineId = "whisper-large-v3-turbo-q5_0",
                    NumThreads = 8,
                    EnableHotwordBiasing = true,
                },
                BenchmarkedAt = DateTimeOffset.Now,
            }, TestContext.Current.CancellationToken);
        }

        using var app = new SettingsStore(Paths);
        var reloaded = app.Read();

        Assert.Equal("whisper-large-v3-turbo-q5_0", reloaded.Speech.EngineId);
        Assert.Equal(8, reloaded.Speech.NumThreads);
        Assert.True(reloaded.Speech.EnableHotwordBiasing);
        Assert.NotNull(reloaded.BenchmarkedAt);
    }

    [Fact]
    public async Task WriteIsAtomic_LeavingNoTemporaryFileBehind()
    {
        using var store = new SettingsStore(Paths);

        await store.WriteAsync(new JaneSettings { OnboardingComplete = true }, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(store.Path));
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task CorruptFile_FallsBackToDefaultsAndIsRepairedByTheNextWrite()
    {
        // A settings file truncated by a crash must not stop Jane starting -- it holds the
        // hotkey, the dictionary and the onboarding state at once.
        Directory.CreateDirectory(_root.FullName);
        await File.WriteAllTextAsync(Paths.SettingsFile, "{ this is not json",
            TestContext.Current.CancellationToken);

        using var store = new SettingsStore(Paths);
        Assert.Equal("parakeet-tdt-0.6b-v2-int8", store.Read().Speech.EngineId);

        await store.WriteAsync(store.Current with { OnboardingComplete = true }, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Paths.SettingsFile, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("onboardingComplete").GetBoolean());
    }

    [Fact]
    public async Task RoundTripPreservesEveryNestedSection()
    {
        var original = new JaneSettings
        {
            Speech = new SpeechSettings("whisper-large-v3-turbo-q8_0", 2, true, 2.5f, "whisper-large-v3-turbo-q8_0"),
            Hotkey = new HotkeySettings(0x14, HotkeyMode.Toggle, 250, 60_000, EnabledInGame: true),
            Llm = new LlmSettings("m-gpu", "m-cpu", "90s", 90, 4096, 2, InGameBehaviour.UseCpuLlm, Enabled: false),
            Overlay = new OverlaySettings(Visible: false, ShowContextIndicator: false),
            Gpu = new GpuSettings(1024, 55, false, false, false),
            MicrophoneDeviceId = "{0.0.1.00000000}",
            OnboardingComplete = true,
        };

        using var store = new SettingsStore(Paths);
        await store.WriteAsync(original, TestContext.Current.CancellationToken);

        using var reopened = new SettingsStore(Paths);
        Assert.Equal(original, reopened.Read());
    }

    [Fact]
    public async Task FileChangeRaisesChanged_SoARebenchNeedsNoRestart()
    {
        using var store = new SettingsStore(Paths);
        await store.WriteAsync(new JaneSettings(), TestContext.Current.CancellationToken);
        store.StartWatching();

        var signalled = new TaskCompletionSource<JaneSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, s) => signalled.TrySetResult(s);

        using (var other = new SettingsStore(Paths))
        {
            await other.UpdateAsync(s => s with { Speech = s.Speech with { NumThreads = 8 } },
                TestContext.Current.CancellationToken);
        }

        var observed = await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(8, observed.Speech.NumThreads);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
