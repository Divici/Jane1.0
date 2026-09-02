namespace Jane.Core.Abstractions;

/// <summary>Where a formatting request should run, if anywhere.</summary>
public enum LlmRoute
{
    /// <summary>
    /// Skip the LLM entirely and inject raw ASR text. The default while a game is detected.
    /// </summary>
    /// <remarks>
    /// Not a degraded mode so much as a different trade. Parakeet already emits punctuation and
    /// casing, so raw output is presentable text -- and an eight-thread CPU prefill burst is a
    /// bigger hit to a running game than the GPU call it was meant to avoid.
    /// </remarks>
    Skip,

    /// <summary>The GPU-resident model. The everyday route.</summary>
    Gpu,

    /// <summary>
    /// A CPU-pinned model. Opt-in only, for users who want formatting more than frames.
    /// </summary>
    Cpu,
}

/// <summary>Why the governor decided a game is running. Combined, never trusted individually.</summary>
[Flags]
public enum GameSignals
{
    None = 0,

    /// <summary>
    /// <c>SHQueryUserNotificationState</c> reported a fullscreen or presentation app.
    /// </summary>
    /// <remarks>
    /// Only sees D3D *exclusive* fullscreen. Most modern games run borderless-windowed, so this
    /// signal alone would miss the game entirely -- which is precisely why there are three.
    /// </remarks>
    NotificationState = 1,

    /// <summary>The foreground window's rectangle covers its whole monitor. Catches borderless-windowed.</summary>
    FullscreenGeometry = 2,

    /// <summary>NVML reports the GPU busy, or too little free VRAM to load into without contending.</summary>
    GpuBusy = 4,
}

/// <param name="Signals">Which of the three fired. Printed by `bench -- route` so a decision is checkable.</param>
/// <param name="Reason">One sentence a person can read.</param>
/// <param name="FreeVramBytes">Null when NVML is unavailable.</param>
public sealed record RoutingDecision(
    LlmRoute Route,
    GameSignals Signals,
    string Reason,
    long? FreeVramBytes = null,
    uint? GpuUtilisationPercent = null,
    string? ForegroundProcess = null)
{
    public bool GameDetected => Signals != GameSignals.None;
}

/// <summary>
/// Decides whether Jane may use the GPU right now.
/// </summary>
/// <remarks>
/// The user's hard constraint is that the machine's resources stay free: "Outside of that, I'm
/// still able to game or use all of my computer's resources fully and freely at all times." This
/// interface is where that promise is kept or broken.
/// </remarks>
public interface IGpuGovernor
{
    /// <summary>Evaluates all three signals and returns a route with the inputs that produced it.</summary>
    RoutingDecision Decide();

    /// <summary>True while a game is detected, so CPU-bound work can drop its thread priority.</summary>
    bool IsGameRunning { get; }
}
