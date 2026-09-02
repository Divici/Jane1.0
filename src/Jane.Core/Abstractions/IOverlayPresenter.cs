namespace Jane.Core.Abstractions;

/// <summary>
/// What the floating pill is showing.
/// </summary>
/// <remarks>
/// This is the only wire between the pipeline's states and the overlay's visuals. Phase 5's
/// orchestrator maps its state machine onto these; the overlay knows nothing else about it.
/// </remarks>
public enum OverlayState
{
    /// <summary>Hidden entirely. Not a transparent window -- nothing on screen.</summary>
    Idle,

    /// <summary>Capturing. Shows a live waveform driven by the audio source.</summary>
    Listening,

    /// <summary>Transcribing or formatting. The user cannot tell the two apart and does not need to.</summary>
    Thinking,

    /// <summary>Text is going into the target window.</summary>
    Injecting,

    /// <summary>
    /// A live selection exists but neither UIA nor the clipboard probe could read it, so Edit
    /// Mode cannot run here. Shown explicitly rather than silently dictating the spoken command
    /// into the document.
    /// </summary>
    EditModeUnavailable,

    /// <summary>Something failed. Carries a message.</summary>
    Error,
}

/// <param name="Message">Human-readable, shown in the pill and exposed as the accessible name.</param>
/// <param name="Level">Audio level in [0, 1] for the waveform. Ignored outside Listening.</param>
public sealed record OverlayStatus(OverlayState State, string? Message = null, float Level = 0f)
{
    public static OverlayStatus Idle { get; } = new(OverlayState.Idle);
}

/// <summary>
/// The floating status pill.
/// </summary>
/// <remarks>
/// The single hard requirement: nothing here may ever take focus. Jane types into whatever the
/// user was already using, so a moment of stolen focus does not just look wrong -- it sends the
/// dictation to the wrong window.
/// </remarks>
public interface IOverlayPresenter
{
    OverlayStatus Status { get; }

    /// <summary>Safe to call from any thread; marshals to the UI dispatcher itself.</summary>
    void Show(OverlayStatus status);

    void Hide();
}
