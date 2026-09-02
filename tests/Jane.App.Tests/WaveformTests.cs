using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jane.App.Overlay;

namespace Jane.App.Tests;

/// <summary>
/// The waveform runs inside the render pass, where an exception has no caller left to catch it:
/// a throw from <c>OnRender</c> takes down the dispatcher, and with it the whole tray app, in the
/// middle of a dictation. So the interesting cases are all the degenerate ones.
/// </summary>
public sealed class WaveformTests
{
    private const int Width = 50;
    private const int Height = 16;

    [Fact]
    public void RendersWithoutThrowingOnAnAllSilenceBuffer()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var waveform = Arranged();

            // A muted mic, or a hotkey held with nothing said, produces exactly this.
            for (var i = 0; i < 200; i++)
            {
                waveform.Push(0f);
            }

            Render(waveform);
        });
    }

    [Fact]
    public void RendersWithoutThrowingOnLevelsTheAudioPathShouldNeverProduce()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var waveform = Arranged();

            // NaN is the realistic one: a level computed as a ratio over a zero-length frame is
            // NaN, and NaN reaching DrawRoundedRectangle throws.
            foreach (var level in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -7f, 12f })
            {
                waveform.Push(level);
            }

            Render(waveform);

            var bounds = VisualTreeHelper.GetContentBounds(waveform);
            Assert.False(double.IsNaN(bounds.Height), "A hostile level leaked into the drawn geometry.");
            Assert.InRange(bounds.Height, 0, Height);
        });
    }

    [Fact]
    public void SilenceDrawsAFlatLineRatherThanNothing()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var waveform = Arranged();
            for (var i = 0; i < 40; i++)
            {
                waveform.Push(0f);
            }

            Render(waveform);
            var bounds = VisualTreeHelper.GetContentBounds(waveform);

            // A control that draws nothing at silence reads as broken, not as quiet.
            Assert.False(bounds.IsEmpty);
            Assert.InRange(bounds.Height, 1.5, 3.0);
        });
    }

    [Fact]
    public void LoudAudioDrawsTallerBarsThanSilence()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var waveform = Arranged();
            for (var i = 0; i < 40; i++)
            {
                waveform.Push(1f);
            }

            Render(waveform);
            var loud = VisualTreeHelper.GetContentBounds(waveform).Height;

            waveform.Reset();
            Render(waveform);
            var silent = VisualTreeHelper.GetContentBounds(waveform).Height;

            Assert.True(loud > silent + 6, $"Loud bars ({loud}) barely differ from silence ({silent}).");
        });
    }

    [Fact]
    public void ResetFlattensTheTraceSoTheNextDictationStartsClean()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            var waveform = Arranged();
            for (var i = 0; i < 40; i++)
            {
                waveform.Push(1f);
            }

            waveform.Reset();
            Render(waveform);

            Assert.InRange(VisualTreeHelper.GetContentBounds(waveform).Height, 1.5, 3.0);
        });
    }

    private static WaveformControl Arranged() => new() { Height = Height, Fill = Brushes.White };

    /// <remarks>
    /// The measure/arrange pass has to happen <em>after</em> the pushes, not before: an element
    /// outside a live visual tree has no layout manager to service the invalidation Push raises,
    /// so Arrange is what actually re-runs OnRender. Rendering an element arranged beforehand
    /// would silently replay stale, all-zero content and pass every case here.
    /// </remarks>
    private static void Render(WaveformControl waveform)
    {
        waveform.Measure(new Size(Width, Height));
        waveform.Arrange(new Rect(0, 0, Width, Height));

        // RenderTargetBitmap.Render walks the visual synchronously on this thread, so a throw
        // from OnRender surfaces here rather than as an unhandled dispatcher fault.
        new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32).Render(waveform);
    }
}
