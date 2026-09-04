using Jane.App.Composition;
using Jane.Core.Abstractions;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Windows.Hotkeys;

namespace Jane.App.Tests;

/// <summary>
/// A setting that is written but never applied is worse than a setting that does not exist: the
/// user changes it, watches it not work, and concludes the whole app is broken.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because exactly that shipped. The settings window wrote the new hotkey into
/// SQLite and nothing ever read it back -- <c>JaneHost.Create</c> composed the keyboard hook from
/// the Phase 1 <c>settings.json</c>, which only ever seeds the database and is never written by
/// the settings window. So a rebind survived neither the moment nor a restart, and Right Ctrl
/// stayed bound forever.
/// </para>
/// <para>
/// Two separate defects, and both are covered here, because fixing either alone leaves the bug:
/// <see cref="TheDatabaseIsTheAuthorityForStartupSettings"/> pins which store the app composes
/// from, and the rest pin that a change reaches a Jane that is already running.
/// </para>
/// </remarks>
public sealed class LiveSettingsTests
{
    private const int VkRightAlt = 0xA5;
    private const int VkF13 = 0x7C;

    [Fact]
    public void ATextSettingsChangeReachesTheRunningPipeline()
    {
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        var seen = new List<TextSettings>();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, seen.Add);

        live.Apply(new JaneSettings { Text = new TextSettings(AutoSpace: false) });

        // Somebody turning automatic spacing off has just watched Jane put a space where they did
        // not want one. Making them restart to find out whether it helped is the wrong answer.
        Assert.False(Assert.Single(seen).AutoSpace);
    }

    [Fact]
    public void ChangingTheHotkeyReachesTheRunningListener()
    {
        // The user's report, reduced: bind Right Alt, and Jane must be listening for Right Alt
        // without being restarted.
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Apply(new JaneSettings { Hotkey = new HotkeySettings(VirtualKey: VkRightAlt) });

        Assert.Equal(VkRightAlt, hotkeys.Binding.VirtualKey);
    }

    [Fact]
    public void ChangingTheHotkeyCarriesItsModifiers()
    {
        // HotkeySettings originally stored only a virtual key, so an accepted "Ctrl + Shift + F13"
        // was persisted as a bare F13 -- a different, and much worse, binding than the one the
        // validator approved.
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Apply(new JaneSettings
        {
            Hotkey = new HotkeySettings(VirtualKey: VkF13, Modifiers: [0x11, 0x10]),
        });

        Assert.Equal(VkF13, hotkeys.Binding.VirtualKey);
        Assert.Equal([0x11, 0x10], hotkeys.Binding.RequiresModifiers);
    }

    [Fact]
    public void ChangingTheModeAndTheMinimumHoldReachesTheRunningListener()
    {
        // Mode had a live path already, through the tray. Minimum hold had none: it was baked
        // into HotkeyOptions at construction and the slider in settings moved nothing.
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Apply(new JaneSettings
        {
            Hotkey = new HotkeySettings(Mode: HotkeyMode.Toggle, MinimumHoldMs: 50),
        });

        Assert.Equal(HotkeyMode.Toggle, hotkeys.Mode);
        Assert.Equal(TimeSpan.FromMilliseconds(50), hotkeys.Options.MinimumHold);
    }

    [Fact]
    public void ChangingTheMicrophoneReachesTheRunningCapture()
    {
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Apply(new JaneSettings
        {
            MicrophoneDeviceId = "{0.0.1.00000000}.{some-device}",
            Microphone = new MicrophoneSettings(Activation: MicrophoneActivation.AlwaysOpen),
        });

        Assert.Equal("{0.0.1.00000000}.{some-device}", microphone.Routing?.DeviceId);
        Assert.Equal(MicrophoneActivation.AlwaysOpen, microphone.Routing?.Activation);
    }

    [Fact]
    public async Task AWriteThroughTheRepositoryIsAppliedWithoutAnyoneCallingApply()
    {
        // The wire that was missing entirely. SettingsRepository.Changed already fired on every
        // write; nothing was listening to it.
        using var jane = new TempJane();
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        using var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Attach(jane.Settings);

        await jane.Settings.UpdateAsync(
            s => s with { Hotkey = s.Hotkey with { VirtualKey = VkRightAlt } },
            TestContext.Current.CancellationToken);

        Assert.Equal(VkRightAlt, hotkeys.Binding.VirtualKey);
    }

    [Fact]
    public async Task DetachingStopsFurtherSettingsFromBeingApplied()
    {
        using var jane = new TempJane();
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        var live = new LiveSettings(hotkeys, microphone, _ => { }, _ => { });

        live.Attach(jane.Settings);
        live.Dispose();

        await jane.Settings.UpdateAsync(
            s => s with { Hotkey = s.Hotkey with { VirtualKey = VkRightAlt } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HotkeyBinding.VkRightControl, hotkeys.Binding.VirtualKey);
    }

    [Fact]
    public void TheOverlayIsToldAboutItsOwnSettings()
    {
        using var hotkeys = new LowLevelKeyboardHook(HotkeyBinding.Default, HotkeyMode.Hold);
        var microphone = new RecordingAudioSource();
        var seen = new List<OverlaySettings>();
        using var live = new LiveSettings(hotkeys, microphone, seen.Add, _ => { });

        live.Apply(new JaneSettings { Overlay = new OverlaySettings(Visible: false) });

        Assert.False(Assert.Single(seen).Visible);
    }

    [Fact]
    public async Task TheDatabaseIsTheAuthorityForStartupSettings()
    {
        // The root cause of the reported bug, pinned directly. `bench` writes settings.json and
        // the settings window writes SQLite; when the two disagree about a value a person can
        // edit, the one they edited has to win, or their change silently never happens.
        using var jane = new TempJane();

        var benchFile = new SettingsStore(jane.Paths);
        await benchFile.WriteAsync(
            new JaneSettings { Hotkey = new HotkeySettings(VirtualKey: HotkeyBinding.VkRightControl) },
            TestContext.Current.CancellationToken);

        await jane.Settings.UpdateAsync(
            s => s with { Hotkey = s.Hotkey with { VirtualKey = VkRightAlt } },
            TestContext.Current.CancellationToken);

        var effective = JaneHost.ReadStartupSettings(benchFile, jane.Settings);

        Assert.Equal(VkRightAlt, effective.Hotkey.VirtualKey);
    }

    [Fact]
    public async Task TheBenchFileStillDecidesTheSpeechEngine()
    {
        // The other half of the same rule. `bench` is the only thing that chooses an engine, it
        // writes to settings.json, and it runs in another process while Jane is closed -- so the
        // engine has to be read from the file even though the hotkey is read from the database.
        using var jane = new TempJane();

        var benchFile = new SettingsStore(jane.Paths);
        await benchFile.WriteAsync(
            new JaneSettings
            {
                Speech = new SpeechSettings(EngineId: "whisper-small-int8", NumThreads: 8),
                BenchmarkedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);

        var effective = JaneHost.ReadStartupSettings(benchFile, jane.Settings);

        Assert.Equal("whisper-small-int8", effective.Speech.EngineId);
        Assert.Equal(8, effective.Speech.NumThreads);
    }

    [Fact]
    public async Task AStaleBenchFileDoesNotUndoAnEngineChosenInSettings()
    {
        // The reason the two stores cannot simply be merged. `bench` writes the whole settings
        // object, so its file keeps a copy of every value as they were the day it ran; letting
        // that file win unconditionally is how a months-old benchmark silently reverts an engine
        // the user picked this morning. The recorded run time is the tiebreak.
        using var jane = new TempJane();

        var benchFile = new SettingsStore(jane.Paths);
        await benchFile.WriteAsync(
            new JaneSettings
            {
                Speech = new SpeechSettings(EngineId: "whisper-small-int8"),
                BenchmarkedAt = DateTimeOffset.UtcNow.AddDays(-30),
            },
            TestContext.Current.CancellationToken);

        await jane.Settings.UpdateAsync(
            s => s with
            {
                Speech = s.Speech with { EngineId = "parakeet-tdt-0.6b-v2-int8" },
                BenchmarkedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);

        var effective = JaneHost.ReadStartupSettings(benchFile, jane.Settings);

        Assert.Equal("parakeet-tdt-0.6b-v2-int8", effective.Speech.EngineId);
    }

    [Fact]
    public void NoBenchFileAtAllIsNormalAndCostsNothing()
    {
        // A fresh install, or a user who never ran `bench`. The database's defaults stand.
        using var jane = new TempJane();

        var benchFile = new SettingsStore(jane.Paths);

        var effective = JaneHost.ReadStartupSettings(benchFile, jane.Settings);

        Assert.Equal(new SpeechSettings().EngineId, effective.Speech.EngineId);
    }

    private sealed class RecordingAudioSource : IAudioSource
    {
        public MicrophoneRouting? Routing { get; private set; }

        public AudioSourceState State { get; } = new(IsOpen: false, IsCapturing: false, null, null);

        public event EventHandler<AudioSourceState>? StateChanged;

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Arm() => StateChanged?.Invoke(this, State);

        public CapturedAudio Stop(CaptureStopReason reason) =>
            new(ReadOnlyMemory<float>.Empty, 0, reason, DateTimeOffset.UtcNow);

        public void Reconfigure(MicrophoneRouting routing) => Routing = routing;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
