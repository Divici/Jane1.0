using System.Text.Json.Serialization;
using Jane.Core.Abstractions;

namespace Jane.Core.Settings;

/// <summary>What Jane does with the LLM while a game is running.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InGameBehaviour>))]
public enum InGameBehaviour
{
    /// <summary>
    /// Default. Inject raw ASR text and skip the LLM entirely. Parakeet already emits punctuation
    /// and casing, and an eight-thread CPU prefill burst is a bigger hit to a running game than
    /// the GPU call it was meant to avoid.
    /// </summary>
    SkipLlm,

    /// <summary>Opt-in: route formatting to a CPU-pinned model. Slower, and it costs cores.</summary>
    UseCpuLlm,
}

/// <param name="EngineId">Chosen by `bench`, read by the app at startup and on file change.</param>
/// <param name="EnableHotwordBiasing">
/// Only ever set true by `bench`, and only when biasing measured inside the latency budget --
/// contextual biasing forces beam search in place of greedy decoding.
/// </param>
public sealed record SpeechSettings(
    string EngineId = "parakeet-tdt-0.6b-v2-int8",
    int NumThreads = 4,
    bool EnableHotwordBiasing = false,
    float HotwordBoost = 1.5f,
    string? WhisperModelId = null);

/// <param name="MinimumHoldMs">
/// Holds shorter than this cancel silently. Right Ctrl is a common game bind, so a stray tap must
/// cost nothing at all -- no overlay, no models, no text.
/// </param>
/// <param name="MaxToggleDurationMs">Toggle mode auto-stops here and runs the pipeline.</param>
/// <param name="Modifiers">
/// Modifier virtual-keys that must also be held, empty for the bare-modifier default.
/// </param>
public sealed record HotkeySettings(
    int VirtualKey = HotkeyBinding.VkRightControl,
    HotkeyMode Mode = HotkeyMode.Hold,
    int MinimumHoldMs = 300,
    int MaxToggleDurationMs = 300_000,
    bool EnabledInGame = false,
    IReadOnlyList<int>? Modifiers = null)
{
    /// <summary>
    /// The binding this describes.
    /// </summary>
    /// <remarks>
    /// <see cref="Modifiers"/> arrived late, which is why it is nullable: settings rows written
    /// before it existed have no value for it, and a null there means the bare key the user
    /// originally bound rather than an error.
    /// </remarks>
    public HotkeyBinding ToBinding() => new(VirtualKey, Modifiers ?? []);
}

/// <param name="Activation">When Jane holds a capture stream open. See the enum -- the default
/// is the way it is because of Bluetooth headsets, not because of the mic-in-use indicator.</param>
/// <param name="IdleReleaseSeconds">
/// How long the device stays open after a dictation under
/// <see cref="MicrophoneActivation.WhileDictating"/>.
/// </param>
public sealed record MicrophoneSettings(
    MicrophoneActivation Activation = MicrophoneActivation.WhileDictating,
    int IdleReleaseSeconds = 8);

/// <param name="IdleUnloadSeconds">
/// How long after the last dictation Jane issues its single explicit unload. The request-level
/// keep_alive is shorter than this on purpose: the timer is the belt, keep_alive the braces.
/// </param>
public sealed record LlmSettings(
    string GpuModel = "jane-qwen3-4b",
    string CpuModel = "jane-qwen3-1.7b",
    string KeepAlive = "180s",
    int IdleUnloadSeconds = 180,
    int NumCtx = 8192,
    int CpuNumThreads = 4,
    InGameBehaviour InGame = InGameBehaviour.SkipLlm,
    bool Enabled = true);

/// <summary>Where the pill sits when nothing is docked over it.</summary>
public enum OverlayAnchor
{
    /// <summary>
    /// Default. Centred above the taskbar, which is where a push-to-talk indicator belongs: it
    /// is on the path between the keyboard and the text you are dictating into.
    /// </summary>
    BottomCentre,

    /// <summary>
    /// The work-area corner nearest the notification area. Out of the way, and next to the tray
    /// icon that owns it.
    /// </summary>
    NearTray,
}

/// <param name="Visible">Off means Jane never draws a pill at all, in any state.</param>
/// <param name="ShowWhenIdle">
/// Whether the pill rests on screen between dictations, naming the hotkey. On by default: a
/// background app that is invisible until you already know how to use it teaches nobody anything.
/// </param>
public sealed record OverlaySettings(
    bool Visible = true,
    bool ShowContextIndicator = true,
    bool ShowWhenIdle = true,
    OverlayAnchor Anchor = OverlayAnchor.BottomCentre);

/// <param name="MinimumFreeVramBytes">
/// Below this, the GPU counts as contended even with no game detected. Loading a 2.5 GB model
/// into the last of someone's VRAM is how you cause a stutter in something else.
/// </param>
public sealed record GpuSettings(
    long MinimumFreeVramBytes = 4L * 1024 * 1024 * 1024,
    int BusyUtilisationPercent = 40,
    bool TrustNotificationState = true,
    bool TrustFullscreenGeometry = true,
    bool TrustNvml = true);

/// <summary>
/// Everything Jane remembers between runs.
/// </summary>
/// <remarks>
/// JSON at <c>%LOCALAPPDATA%\Jane\settings.json</c> from Phase 1, because `bench` and the app both
/// need it before there is a database. Phase 10 migrates it into SQLite and keeps this shape as
/// the in-memory model.
/// </remarks>
public sealed record JaneSettings
{
    /// <summary>Bumped whenever a migration is needed. Read before anything else in the file.</summary>
    public int SchemaVersion { get; init; } = 1;

    public SpeechSettings Speech { get; init; } = new();

    public HotkeySettings Hotkey { get; init; } = new();

    public LlmSettings Llm { get; init; } = new();

    public OverlaySettings Overlay { get; init; } = new();

    public GpuSettings Gpu { get; init; } = new();

    public MicrophoneSettings Microphone { get; init; } = new();

    /// <summary>Empty means "use the Windows default communications input".</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>Which device to open and when, as the audio source wants it.</summary>
    /// <remarks>
    /// Not serialised. It is a view over two stored values, and letting it be written would put
    /// a second copy of both into the settings table for the next reader to disagree with.
    /// </remarks>
    [JsonIgnore]
    public MicrophoneRouting Routing => new(
        MicrophoneDeviceId,
        Microphone.Activation,
        TimeSpan.FromSeconds(Microphone.IdleReleaseSeconds));

    /// <summary>Set once onboarding completes, so first run is detected without a sentinel file.</summary>
    public bool OnboardingComplete { get; init; }

    /// <summary>Which `bench` run produced the current engine selection. Diagnostic breadcrumb.</summary>
    public DateTimeOffset? BenchmarkedAt { get; init; }
}
