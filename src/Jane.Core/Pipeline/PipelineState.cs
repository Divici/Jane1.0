using Jane.Core.Abstractions;

namespace Jane.Core.Pipeline;

/// <summary>Where a dictation is in its life.</summary>
public enum PipelineState
{
    /// <summary>Nothing in flight. The overlay is hidden entirely.</summary>
    Idle,

    /// <summary>
    /// Key-down seen. Capture is armed and Deep Context has started, but no model work has begun.
    /// Arming is free on purpose: Right Ctrl is a common game bind, so a stray tap must cost
    /// nothing at all.
    /// </summary>
    Arming,

    /// <summary>VAD has confirmed speech and the buffer is filling.</summary>
    Listening,

    /// <summary>ASR is running.</summary>
    Transcribing,

    /// <summary>The LLM is cleaning the transcript. Skipped entirely on the bypass and in-game paths.</summary>
    Formatting,

    /// <summary>Text is going into the target window.</summary>
    Injecting,

    /// <summary>The user abandoned it. Nothing is injected and the buffer is dropped.</summary>
    Cancelled,

    /// <summary>Something went wrong. Carries a <see cref="PipelineFailure"/> and a message.</summary>
    Failed,
}

/// <summary>Every way a dictation can fail. Each maps to designed overlay text.</summary>
public enum PipelineFailure
{
    None,

    /// <summary>No capture device, or it disappeared mid-dictation.</summary>
    NoMicrophone,

    /// <summary>The buffer contained no speech, or the transcript came back empty.</summary>
    NoSpeech,

    /// <summary>The recognition engine could not be loaded.</summary>
    EngineUnavailable,

    /// <summary>The recogniser threw while transcribing.</summary>
    RecognitionFailed,

    /// <summary>Injection was refused: wrong window, dead window, or a modifier still held.</summary>
    InjectionAborted,

    /// <summary>
    /// The hotkey fired before the engine finished loading and the wait expired. Distinct from
    /// <see cref="EngineUnavailable"/>: nothing is broken, the user was simply too quick.
    /// </summary>
    EngineStillLoading,
}

/// <param name="Failure">Meaningful only when <paramref name="State"/> is <see cref="PipelineState.Failed"/>.</param>
/// <param name="Message">Shown to the user. Never an exception's ToString.</param>
public sealed record PipelineStatus(
    PipelineState State,
    PipelineFailure Failure = PipelineFailure.None,
    string? Message = null)
{
    public static PipelineStatus Idle { get; } = new(PipelineState.Idle);

    /// <summary>
    /// The single documented mapping from pipeline states to overlay visuals.
    /// </summary>
    /// <remarks>
    /// This is the only wire between Phase 5's state machine and Phase 4's visuals. Transcribing
    /// and Formatting both read as "thinking" because the user cannot tell them apart and does
    /// not need to; splitting them would only make the pill flicker.
    /// </remarks>
    public OverlayStatus ToOverlayStatus(float level = 0f) => State switch
    {
        PipelineState.Idle => OverlayStatus.Idle,
        PipelineState.Arming => new OverlayStatus(OverlayState.Listening, "Listening", level),
        PipelineState.Listening => new OverlayStatus(OverlayState.Listening, "Listening", level),
        PipelineState.Transcribing => new OverlayStatus(OverlayState.Thinking, "Transcribing"),
        PipelineState.Formatting => new OverlayStatus(OverlayState.Thinking, "Formatting"),
        PipelineState.Injecting => new OverlayStatus(OverlayState.Injecting, "Inserting"),
        PipelineState.Cancelled => OverlayStatus.Idle,
        PipelineState.Failed => new OverlayStatus(OverlayState.Error, Message ?? DefaultMessageFor(Failure)),
        _ => OverlayStatus.Idle,
    };

    public static string DefaultMessageFor(PipelineFailure failure) => failure switch
    {
        PipelineFailure.NoMicrophone => "No microphone. Check Settings > System > Sound > Input.",
        PipelineFailure.NoSpeech => "No speech detected.",
        PipelineFailure.EngineUnavailable => "Speech model unavailable. Open Jane's settings to download it.",
        PipelineFailure.RecognitionFailed => "Transcription failed.",
        PipelineFailure.InjectionAborted => "Could not insert the text there.",
        PipelineFailure.EngineStillLoading => "Still starting up. Try again in a moment.",
        _ => "Something went wrong.",
    };
}
