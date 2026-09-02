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
public sealed record HotkeySettings(
    int VirtualKey = HotkeyBinding.VkRightControl,
    HotkeyMode Mode = HotkeyMode.Hold,
    int MinimumHoldMs = 300,
    int MaxToggleDurationMs = 300_000,
    bool EnabledInGame = false);

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

public sealed record OverlaySettings(bool Visible = true, bool ShowContextIndicator = true);

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

    /// <summary>Empty means "use the Windows default communications input".</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>Set once onboarding completes, so first run is detected without a sentinel file.</summary>
    public bool OnboardingComplete { get; init; }

    /// <summary>Which `bench` run produced the current engine selection. Diagnostic breadcrumb.</summary>
    public DateTimeOffset? BenchmarkedAt { get; init; }
}
