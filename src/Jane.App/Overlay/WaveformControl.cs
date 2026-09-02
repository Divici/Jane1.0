using System.Windows;
using System.Windows.Media;

namespace Jane.App.Overlay;

/// <summary>
/// A scrolling bar waveform, driven by audio level.
/// </summary>
/// <remarks>
/// A real waveform, not a spinner. A spinner says "something is happening"; a waveform says
/// "your microphone is hearing you", which is the one question a user has while holding a
/// push-to-talk key. Silence must therefore look like silence -- a flat row of dots -- rather
/// than like a dead control, so bars never collapse to nothing.
/// <para>
/// It draws only when a level is pushed, so it costs nothing while the pill is hidden. The
/// window runs the pump, and only while listening.
/// </para>
/// </remarks>
public sealed class WaveformControl : FrameworkElement
{
    /// <summary>Enough bars to read as a wave, few enough to stay inside a 28 px pill.</summary>
    public const int BarCount = 13;

    private const double BarWidth = 2.0;
    private const double BarGap = 2.0;

    /// <summary>Silence still draws this much, so an idle mic reads as a flat line, not a bug.</summary>
    private const double MinBarHeight = 2.0;

    /// <summary>
    /// How far each new bar travels toward the incoming level. Raw levels jitter frame to frame;
    /// easing turns that into a wave without lagging far enough behind speech to feel detached.
    /// </summary>
    private const double Attack = 0.55;

    private readonly double[] _bars = new double[BarCount];

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(WaveformControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>
    /// Advances the trace by one bar with the given level in [0, 1].
    /// </summary>
    /// <remarks>
    /// Hostile input is normalised rather than rejected. The level arrives from an audio path
    /// that can legitimately produce NaN -- a level computed as a ratio over a zero-length frame
    /// does -- and NaN reaching <c>DrawRoundedRectangle</c> throws inside the render pass, where
    /// there is no caller left to catch it.
    /// </remarks>
    public void Push(float level)
    {
        var target = Normalise(level);

        // Scroll one bar left; the newest bar is always the rightmost.
        Array.Copy(_bars, 1, _bars, 0, BarCount - 1);

        var previous = _bars[BarCount - 2];
        _bars[BarCount - 1] = previous + ((target - previous) * Attack);

        InvalidateVisual();
    }

    /// <summary>Flattens the trace. Called when listening ends so the next dictation starts clean.</summary>
    public void Reset()
    {
        Array.Clear(_bars);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = (BarCount * BarWidth) + ((BarCount - 1) * BarGap);
        var height = double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height;

        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (!IsDrawable(width) || !IsDrawable(height))
        {
            return;
        }

        var brush = Fill ?? JanePalette.AccentBrush;
        var centre = height / 2;
        var travel = Math.Max(0, height - MinBarHeight);
        var radius = BarWidth / 2;

        for (var i = 0; i < BarCount; i++)
        {
            var x = i * (BarWidth + BarGap);
            if (x + BarWidth > width)
            {
                break;
            }

            var barHeight = MinBarHeight + (_bars[i] * travel);
            var rect = new Rect(x, centre - (barHeight / 2), BarWidth, barHeight);

            drawingContext.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }

    private static bool IsDrawable(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Normalise(float level)
    {
        if (float.IsNaN(level) || float.IsInfinity(level))
        {
            return 0;
        }

        // Speech levels cluster near the bottom of a linear scale, so a linear bar spends most of
        // its life looking flat. The square root spreads quiet speech across the pill's height.
        return Math.Sqrt(Math.Clamp(level, 0f, 1f));
    }
}
