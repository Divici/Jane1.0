using Jane.Core.Abstractions;
using Jane.Core.Pipeline;

namespace Jane.App.Overlay;

/// <summary>
/// Feeds the microphone's level to the overlay for as long as a dictation is listening.
/// </summary>
/// <remarks>
/// <para>
/// The waveform, its 30 fps render pump, and the decayed peak meter that drives it were all
/// built and all worked. Nothing connected them. The overlay's level was refreshed only by
/// <c>OverlayWindow.Apply</c>, whose only caller was the pipeline's state-changed event -- which
/// fires once per state, not thirty times a second. So the bars were drawn once, at whatever the
/// level happened to be in the instant the key went down, which is silence, and then decayed to a
/// flat row of dots and stayed there for the whole dictation.
/// </para>
/// <para>
/// A waveform that does not move is worse than no waveform: the one question somebody has while
/// holding a push-to-talk key is whether the microphone is hearing them, and a dead trace answers
/// it wrongly.
/// </para>
/// <para>
/// The pump runs only while listening. Left running it would be a timer waking the UI thread
/// thirty times a second forever, on an app whose entire idle budget is a fraction of one core.
/// </para>
/// </remarks>
public sealed class OverlayLevelPump : IDisposable
{
    /// <summary>~30 fps, matching the window's own render pump. Faster would draw nothing new.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(33);

    private readonly IOverlayPresenter _overlay;
    private readonly Func<float> _level;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private bool _running;
    private bool _disposed;

    public OverlayLevelPump(IOverlayPresenter overlay, Func<float> level, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(level);

        _overlay = overlay;
        _level = level;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Starts or stops the pump to match the pipeline. Subscribe this to StateChanged.
    /// </summary>
    /// <remarks>
    /// Call this <em>after</em> the status itself has been published to the presenter. The pump
    /// republishes what the presenter already holds rather than inventing a status of its own,
    /// which is what stops a tick that lands late from resurrecting "Listening" over the top of
    /// "Transcribing".
    /// </remarks>
    public void OnState(PipelineStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var listening = status.State is PipelineState.Arming or PipelineState.Listening;

        lock (_gate)
        {
            if (_disposed || listening == _running)
            {
                return;
            }

            _running = listening;
            _timer.Change(
                listening ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
                listening ? _interval : Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
        }

        _timer.Dispose();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_running || _disposed)
            {
                return;
            }
        }

        // Republish whatever the presenter is already showing, with a fresh level. Anything else
        // would mean the pump deciding what the pill says, and a pump that can say "Listening"
        // is a pump that can resurrect it over the top of "Transcribing" one tick too late.
        var current = _overlay.Status;
        if (current.State != OverlayState.Listening)
        {
            return;
        }

        _overlay.Show(current with { Level = _level() });
    }
}
