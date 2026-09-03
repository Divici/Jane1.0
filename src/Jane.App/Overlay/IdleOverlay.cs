using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Settings;

namespace Jane.App.Overlay;

/// <summary>
/// Decides what the pill shows when the pipeline is doing nothing.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline only ever reports <see cref="OverlayState.Idle"/>, which the window renders by
/// hiding itself. That leaves Jane with no resting presence at all: nothing on screen says it is
/// running, which key it is listening for, or that it has been paused from the tray. Somebody who
/// rebinds the hotkey and forgets what they chose has nowhere to look but the settings window.
/// </para>
/// <para>
/// So idle is translated into <see cref="OverlayState.Ready"/> here, on the way to the presenter.
/// It is a pure function of four inputs precisely so the decision can be argued with in a test
/// rather than inferred from a screenshot -- and because "show nothing" has to stay reachable:
/// the pill sits over whatever the user is actually doing, all day, and some people will want it
/// gone.
/// </para>
/// </remarks>
public static class IdleOverlay
{
    /// <summary>
    /// The status to present, given what the pipeline said.
    /// </summary>
    /// <param name="pipeline">The pipeline's own status, returned untouched unless it is idle.</param>
    /// <param name="overlay">The user's overlay settings.</param>
    /// <param name="binding">The hotkey, so the resting pill can name it.</param>
    /// <param name="paused">Whether the tray's Pause item is on.</param>
    /// <param name="fullscreen">
    /// Whether a game, a video or a presentation currently owns the screen. Only the resting pill
    /// yields to it: a dictation somebody deliberately started still shows its status, because
    /// they asked for something to happen and need to see whether it did.
    /// </param>
    public static OverlayStatus For(
        OverlayStatus pipeline,
        OverlaySettings overlay,
        HotkeyBinding binding,
        bool paused,
        bool fullscreen = false)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(binding);

        if (pipeline.State != OverlayState.Idle)
        {
            return pipeline;
        }

        // The pill is topmost and, since it started resting on screen rather than appearing for
        // the second and a half of a dictation, it would otherwise sit on top of a game all
        // evening.
        if (!overlay.Visible || !overlay.ShowWhenIdle || fullscreen)
        {
            return OverlayStatus.Idle;
        }

        // Paused is the case that most needs saying. Without it the pill invites a key press that
        // does nothing, and the user's conclusion is that Jane is broken rather than paused.
        return new OverlayStatus(
            OverlayState.Ready,
            paused
                ? "Jane is paused"
                : $"Hold {KeyNames.Describe(binding)} to dictate");
    }
}
