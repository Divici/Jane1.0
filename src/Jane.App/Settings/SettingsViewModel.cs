using System.Collections.ObjectModel;
using System.Globalization;
using Jane.App.Controls;
using Jane.Core.Abstractions;
using Jane.Core.Instructions;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Core.Vocabulary;
using Jane.Speech;

namespace Jane.App.Settings;

/// <param name="Id">The value stored in <c>speech.engineId</c>, and what `bench` writes there.</param>
public sealed record EngineChoice(string Id, string Name, string Description, ModelAsset Asset)
{
    public override string ToString() => Name;
}

/// <summary>A hotkey mode with the sentence that explains what choosing it means.</summary>
public sealed record ModeChoice(HotkeyMode Mode, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>What Jane does with the LLM while a game is running, in words.</summary>
public sealed record InGameChoice(InGameBehaviour Behaviour, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>
/// Everything the settings window edits, and the one place a change becomes a row in SQLite.
/// </summary>
/// <remarks>
/// <para>
/// Every setter writes. There is no Save button and no dirty state, because a settings screen
/// with an unsaved-changes trap is a settings screen that loses somebody's dictionary entry when
/// they close it -- and because <see cref="SettingsRepository"/> writes the whole object inside
/// one transaction, so a write is cheap and atomic rather than a diff.
/// </para>
/// <para>
/// <see cref="LastWrite"/> exists so a caller can wait for the write it just caused. The window
/// never awaits it; the tests always do, because "the value reached SQLite" is the only assertion
/// worth making about a settings window.
/// </para>
/// <para>
/// The one setting that is not merely stored is overlay visibility: the pill has to be told, or
/// it stays on screen until the next restart and the toggle looks broken. That arrives as a
/// callback rather than as an <c>IOverlayPresenter</c>, because a settings window that holds the
/// presenter could show the pill, and it has no business doing that.
/// </para>
/// </remarks>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsRepository _settings;
    private readonly IModelProvisioner _provisioner;
    private readonly IMicrophoneCatalog _microphones;
    private readonly Action<bool>? _overlayVisibilityChanged;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private JaneSettings _current;
    private HotkeyBinding _hotkey;
    private HotkeyValidation _hotkeyStatus;
    private MicrophoneChoice? _microphone;
    private bool _disposed;

    public SettingsViewModel(
        SettingsRepository settings,
        UserDictionary dictionary,
        CustomInstructions instructions,
        IModelProvisioner provisioner,
        IMicrophoneCatalog microphones,
        Blocklist blocklist,
        Action<bool>? overlayVisibilityChanged = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(microphones);
        ArgumentNullException.ThrowIfNull(blocklist);

        _settings = settings;
        _provisioner = provisioner;
        _microphones = microphones;
        _overlayVisibilityChanged = overlayVisibilityChanged;

        _current = settings.Read();
        _hotkey = new HotkeyBinding(_current.Hotkey.VirtualKey, []);
        _hotkeyStatus = HotkeyValidator.Validate(_hotkey);

        Dictionary = new DictionaryEditor(dictionary);
        Instructions = new InstructionsEditor(instructions);
        DeepContext = new BlocklistInspector(blocklist);

        Microphones = microphones.List();
        _microphone = Microphones.FirstOrDefault(m => m.Id == _current.MicrophoneDeviceId)
                      ?? Microphones.FirstOrDefault();

        foreach (var row in BuildModelRows())
        {
            Models.Add(row);
        }
    }

    /// <summary>The write in flight, or a completed task. Awaited by tests, ignored by the window.</summary>
    public Task LastWrite { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- dictation

    /// <summary>The key Jane listens for. Changed only through <see cref="TryRebind"/>.</summary>
    public HotkeyBinding Hotkey
    {
        get => _hotkey;
        private set
        {
            if (Set(ref _hotkey, value))
            {
                Raise(nameof(HotkeyDescription));
            }
        }
    }

    public string HotkeyDescription => KeyNames.Describe(Hotkey);

    /// <summary>The verdict on the current binding, shown live under the picker.</summary>
    public HotkeyValidation HotkeyStatus
    {
        get => _hotkeyStatus;
        private set
        {
            if (Set(ref _hotkeyStatus, value))
            {
                Raise(nameof(HotkeyStatusSeverity));
            }
        }
    }

    public Severity HotkeyStatusSeverity => HotkeyStatus.Severity;

    /// <summary>
    /// Applies a proposed binding if Jane can actually have it.
    /// </summary>
    /// <returns>
    /// False when the combination is refused. <see cref="HotkeyStatus"/> then carries the reason,
    /// and nothing has been written.
    /// </returns>
    public bool TryRebind(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var verdict = HotkeyValidator.Validate(binding);
        HotkeyStatus = verdict;

        if (!verdict.IsAcceptable)
        {
            return false;
        }

        Hotkey = binding;
        Persist(s => s with { Hotkey = s.Hotkey with { VirtualKey = binding.VirtualKey } });
        return true;
    }

    public IReadOnlyList<ModeChoice> Modes { get; } =
    [
        new(HotkeyMode.Hold, "Hold to talk",
            "Hold the key, speak, let go. The dictation ends when you release, which is the mode Aqua documents and the one that cannot be left running by accident."),
        new(HotkeyMode.Toggle, "Press to start and stop",
            "One press starts, another stops. Better for long dictation; capped by the maximum duration below so a forgotten session cannot run all day."),
    ];

    public HotkeyMode Mode
    {
        get => _current.Hotkey.Mode;
        set
        {
            if (value == _current.Hotkey.Mode)
            {
                return;
            }

            Persist(s => s with { Hotkey = s.Hotkey with { Mode = value } });
            Raise();
            Raise(nameof(ModeChoiceValue));
            Raise(nameof(IsToggleMode));
        }
    }

    /// <summary>The mode as a list item, for a combo box that shows the explanation too.</summary>
    public ModeChoice ModeChoiceValue
    {
        get => Modes.First(m => m.Mode == Mode);
        set
        {
            if (value is not null)
            {
                Mode = value.Mode;
            }
        }
    }

    public bool IsToggleMode => Mode == HotkeyMode.Toggle;

    /// <summary>Holds shorter than this cancel silently -- the whole reason a stray tap costs nothing.</summary>
    public int MinimumHoldMs
    {
        get => _current.Hotkey.MinimumHoldMs;
        set
        {
            var clamped = Math.Clamp(value, 0, 2000);
            if (clamped == _current.Hotkey.MinimumHoldMs)
            {
                return;
            }

            Persist(s => s with { Hotkey = s.Hotkey with { MinimumHoldMs = clamped } });
            Raise();
        }
    }

    public int MaxToggleSeconds
    {
        get => _current.Hotkey.MaxToggleDurationMs / 1000;
        set
        {
            var clamped = Math.Clamp(value, 5, 3600);
            if (clamped * 1000 == _current.Hotkey.MaxToggleDurationMs)
            {
                return;
            }

            Persist(s => s with { Hotkey = s.Hotkey with { MaxToggleDurationMs = clamped * 1000 } });
            Raise();
        }
    }

    // ------------------------------------------------------------------- audio

    public IReadOnlyList<MicrophoneChoice> Microphones { get; private set; }

    public MicrophoneChoice? Microphone
    {
        get => _microphone;
        set
        {
            if (!Set(ref _microphone, value))
            {
                return;
            }

            Persist(s => s with { MicrophoneDeviceId = value?.Id });
        }
    }

    /// <summary>True when Windows reports no capture endpoint at all -- the designed empty state.</summary>
    public bool HasNoMicrophone => Microphones.Count <= 1;

    /// <summary>Re-enumerates. A headset plugged in while the window is open should appear.</summary>
    public void RefreshMicrophones()
    {
        var chosen = _microphone?.Id;
        Microphones = _microphones.List();

        _microphone = Microphones.FirstOrDefault(m => m.Id == chosen) ?? Microphones.FirstOrDefault();

        Raise(nameof(Microphones));
        Raise(nameof(Microphone));
        Raise(nameof(HasNoMicrophone));
    }

    // ------------------------------------------------------------------ engine

    public IReadOnlyList<EngineChoice> Engines { get; } =
    [
        new(ModelCatalog.ParakeetV2Int8.Id, "Parakeet TDT 0.6B v2 (int8)",
            "The default. English only, on the CPU, holding no VRAM. Emits punctuation and casing itself, which is what lets the in-game route skip the language model entirely.",
            ModelCatalog.ParakeetV2Int8),
        new(ModelCatalog.WhisperQuants[0].Id, "Whisper large-v3-turbo (q5_0)",
            "The second engine. Multilingual and larger; kept so the benchmark has something to measure Parakeet against.",
            ModelCatalog.WhisperQuants[0]),
        new(ModelCatalog.WhisperQuants[1].Id, "Whisper large-v3-turbo (q8_0)",
            "The same engine at a heavier quantisation: slightly more accurate, materially slower on a CPU.",
            ModelCatalog.WhisperQuants[1]),
    ];

    public EngineChoice Engine
    {
        get => Engines.FirstOrDefault(e => e.Id == _current.Speech.EngineId) ?? Engines[0];
        set
        {
            if (value is null || value.Id == _current.Speech.EngineId)
            {
                return;
            }

            var whisper = value.Id.StartsWith("whisper", StringComparison.Ordinal);
            Persist(s => s with
            {
                Speech = s.Speech with
                {
                    EngineId = value.Id,
                    WhisperModelId = whisper ? value.Id : null,
                },
            });

            Raise();
            Raise(nameof(EngineWasBenchmarked));
        }
    }

    /// <summary>
    /// Whether the current engine is the one `bench` chose.
    /// </summary>
    /// <remarks>
    /// The plan's whole position on engine selection is that it is measured, not asserted -- "I
    /// don't want to manually do a bench off". Overriding the measurement by hand is allowed, and
    /// the window says when it has happened rather than pretending the choice is arbitrary.
    /// </remarks>
    public bool EngineWasBenchmarked => _current.BenchmarkedAt is not null;

    public string BenchmarkedAtDescription => _current.BenchmarkedAt is { } when
        ? "Chosen by `bench` on " + when.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
        : "No benchmark has been run on this machine yet. Run `dotnet run --project src/Jane.Bench -- bench` to measure both engines.";

    public int NumThreads
    {
        get => _current.Speech.NumThreads;
        set
        {
            var clamped = Math.Clamp(value, 1, Environment.ProcessorCount);
            if (clamped == _current.Speech.NumThreads)
            {
                return;
            }

            Persist(s => s with { Speech = s.Speech with { NumThreads = clamped } });
            Raise();
        }
    }

    public bool HotwordBiasing
    {
        get => _current.Speech.EnableHotwordBiasing;
        set
        {
            if (value == _current.Speech.EnableHotwordBiasing)
            {
                return;
            }

            Persist(s => s with { Speech = s.Speech with { EnableHotwordBiasing = value } });
            Raise();
        }
    }

    public ObservableCollection<ModelRow> Models { get; } = [];

    /// <summary>Re-reads which models are present. Called when the section is opened.</summary>
    public async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        foreach (var row in Models)
        {
            await row.RefreshAsync(cancellationToken);
        }
    }

    // --------------------------------------------------------------- formatting

    public bool LlmEnabled
    {
        get => _current.Llm.Enabled;
        set
        {
            if (value == _current.Llm.Enabled)
            {
                return;
            }

            Persist(s => s with { Llm = s.Llm with { Enabled = value } });
            Raise();
        }
    }

    public IReadOnlyList<InGameChoice> InGameChoices { get; } =
    [
        new(InGameBehaviour.SkipLlm, "Skip formatting entirely",
            "The default, and the one the plan measured. Jane injects the raw transcript, which already has punctuation and casing. An eight-thread CPU prefill is a bigger hit to a running game than the GPU call it would be avoiding."),
        new(InGameBehaviour.UseCpuLlm, "Format on the CPU instead",
            "Formatting still happens, on a CPU-pinned model, and it costs cores. Choose this if you want clean text more than you want frames."),
    ];

    public InGameBehaviour InGame
    {
        get => _current.Llm.InGame;
        set
        {
            if (value == _current.Llm.InGame)
            {
                return;
            }

            Persist(s => s with { Llm = s.Llm with { InGame = value } });
            Raise();
            Raise(nameof(InGameChoiceValue));
        }
    }

    public InGameChoice InGameChoiceValue
    {
        get => InGameChoices.First(c => c.Behaviour == InGame);
        set
        {
            if (value is not null)
            {
                InGame = value.Behaviour;
            }
        }
    }

    /// <summary>How long after the last dictation Jane issues its single explicit unload.</summary>
    public int IdleUnloadSeconds
    {
        get => _current.Llm.IdleUnloadSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 3600);
            if (clamped == _current.Llm.IdleUnloadSeconds)
            {
                return;
            }

            Persist(s => s with { Llm = s.Llm with { IdleUnloadSeconds = clamped } });
            Raise();
        }
    }

    /// <summary>Below this much free VRAM the GPU counts as contended even with no game detected.</summary>
    public double MinimumFreeVramGb
    {
        get => _current.Gpu.MinimumFreeVramBytes / (1024.0 * 1024 * 1024);
        set
        {
            var clamped = Math.Clamp(value, 0, 64);
            var bytes = (long)Math.Round(clamped * 1024 * 1024 * 1024);
            if (bytes == _current.Gpu.MinimumFreeVramBytes)
            {
                return;
            }

            Persist(s => s with { Gpu = s.Gpu with { MinimumFreeVramBytes = bytes } });
            Raise();
        }
    }

    public int BusyUtilisationPercent
    {
        get => _current.Gpu.BusyUtilisationPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped == _current.Gpu.BusyUtilisationPercent)
            {
                return;
            }

            Persist(s => s with { Gpu = s.Gpu with { BusyUtilisationPercent = clamped } });
            Raise();
        }
    }

    public bool TrustNvml
    {
        get => _current.Gpu.TrustNvml;
        set
        {
            if (value == _current.Gpu.TrustNvml)
            {
                return;
            }

            Persist(s => s with { Gpu = s.Gpu with { TrustNvml = value } });
            Raise();
        }
    }

    public bool TrustFullscreenGeometry
    {
        get => _current.Gpu.TrustFullscreenGeometry;
        set
        {
            if (value == _current.Gpu.TrustFullscreenGeometry)
            {
                return;
            }

            Persist(s => s with { Gpu = s.Gpu with { TrustFullscreenGeometry = value } });
            Raise();
        }
    }

    public bool TrustNotificationState
    {
        get => _current.Gpu.TrustNotificationState;
        set
        {
            if (value == _current.Gpu.TrustNotificationState)
            {
                return;
            }

            Persist(s => s with { Gpu = s.Gpu with { TrustNotificationState = value } });
            Raise();
        }
    }

    public bool HotkeyEnabledInGame
    {
        get => _current.Hotkey.EnabledInGame;
        set
        {
            if (value == _current.Hotkey.EnabledInGame)
            {
                return;
            }

            Persist(s => s with { Hotkey = s.Hotkey with { EnabledInGame = value } });
            Raise();
        }
    }

    // ------------------------------------------------------------------ overlay

    /// <summary>Aqua calls this "Show Floating Bar". Off means Jane never draws the pill at all.</summary>
    public bool OverlayVisible
    {
        get => _current.Overlay.Visible;
        set
        {
            if (value == _current.Overlay.Visible)
            {
                return;
            }

            Persist(s => s with { Overlay = s.Overlay with { Visible = value } });
            Raise();

            // The pill has to be told, or it stays on screen until the next restart and the
            // toggle looks like it did nothing.
            _overlayVisibilityChanged?.Invoke(value);
        }
    }

    public bool OverlayShowContextIndicator
    {
        get => _current.Overlay.ShowContextIndicator;
        set
        {
            if (value == _current.Overlay.ShowContextIndicator)
            {
                return;
            }

            Persist(s => s with { Overlay = s.Overlay with { ShowContextIndicator = value } });
            Raise();
        }
    }

    // --------------------------------------------------------------- sub-editors

    public DictionaryEditor Dictionary { get; }

    public InstructionsEditor Instructions { get; }

    public BlocklistInspector DeepContext { get; }

    public IReadOnlyList<AttributionEntry> ModelAttributions => Attributions.Models;

    public IReadOnlyList<AttributionEntry> RuntimeAttributions => Attributions.Runtimes;

    public string NetworkStatement => Attributions.NetworkStatement;

    public string RetentionStatement => Attributions.RetentionStatement;

    /// <summary>
    /// Where the database actually is, so "plaintext until you delete it" is checkable rather
    /// than merely stated. Set by the window from the live <see cref="JaneDatabase"/>.
    /// </summary>
    public string DatabaseLocation { get; set; } = string.Empty;

    /// <summary>The migrations this database has actually run. Shown in About as a version.</summary>
    public string SchemaDescription { get; set; } = string.Empty;

    // ------------------------------------------------------------------ writing

    private IEnumerable<ModelRow> BuildModelRows()
    {
        yield return ModelRow.ForAsset(ModelCatalog.ParakeetV2Int8, _provisioner, required: true);
        yield return ModelRow.ForAsset(ModelCatalog.SileroVad, _provisioner, required: true);

        foreach (var spec in LlmModelCatalog.Required)
        {
            yield return ModelRow.ForLlm(spec, _provisioner, required: false);
        }

        foreach (var quant in ModelCatalog.WhisperQuants)
        {
            yield return ModelRow.ForAsset(quant, _provisioner, required: false);
        }
    }

    /// <summary>
    /// Applies a change to the in-memory model and queues the write behind whatever is already
    /// going.
    /// </summary>
    /// <remarks>
    /// The in-memory update is synchronous so a getter read immediately after a setter sees the
    /// new value -- which is what the bindings, and every reader of this class, assume. The write
    /// is serialised by a semaphore because two settings changed in quick succession would
    /// otherwise race, and the loser would silently rewrite the whole table from a stale object.
    /// </remarks>
    private void Persist(Func<JaneSettings, JaneSettings> update)
    {
        _current = update(_current);
        LastWrite = WriteAsync(_current);
    }

    private async Task WriteAsync(JaneSettings settings)
    {
        await _writeGate.WaitAsync();
        try
        {
            await _settings.WriteAsync(settings, CancellationToken.None);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dictionary.Detach();
        Instructions.Detach();
        _writeGate.Dispose();
    }
}
