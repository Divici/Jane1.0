using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jane.App.Overlay;

// System.Drawing and System.Windows.Media each define Brushes, Point, Size and Color. This file
// lives in both worlds -- it draws with WPF and hands the result to a Win32 icon -- so exactly
// one System.Drawing type is imported, by name.
using Icon = System.Drawing.Icon;

namespace Jane.App.Tray;

/// <summary>
/// Draws Jane's tray icon at every size Windows asks for, in memory.
/// </summary>
/// <remarks>
/// Generated rather than shipped as a file so the icon and the pill cannot drift apart: both take
/// their accent from <see cref="JanePalette"/>, and the mark -- a waveform, the thing Jane
/// actually does -- is the same idea in both places. A filled accent tile with white bars is the
/// one combination that survives both a light and a dark taskbar; a monochrome glyph would
/// disappear into one of them.
/// <para>
/// Windows picks the closest entry to the current DPI, so every size the shell asks for at 100%
/// through 250% scaling is drawn natively rather than resampled.
/// </para>
/// </remarks>
internal static class TrayIconArtwork
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64];

    /// <summary>Relative bar heights, centred -- a waveform read left to right.</summary>
    private static readonly double[] Bars = [0.36, 0.66, 1.0, 0.66, 0.36];

    /// <summary>
    /// The same mark with less detail, for 16 and 20 px entries.
    /// </summary>
    /// <remarks>
    /// Five bars in sixteen pixels leaves each one a pixel and a half wide, which anti-aliases
    /// into a grey smudge. Three fat bars stay legible, and at tray size nobody is counting them.
    /// </remarks>
    private static readonly double[] SmallBars = [0.55, 1.0, 0.55];

    /// <summary>Below this, the detailed mark stops resolving.</summary>
    private const int SmallSizeThreshold = 24;

    /// <summary>Caller owns the returned icon and must dispose it.</summary>
    internal static Icon Create()
    {
        using var stream = new MemoryStream();
        WriteIconFile(stream, Sizes);
        stream.Position = 0;

        return new Icon(stream);
    }

    private static void WriteIconFile(Stream stream, int[] sizes)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var images = sizes.Select(RenderDib).ToArray();

        // ICONDIR
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        // ICONDIRENTRY table, then the images. Offsets are absolute from the file start.
        var offset = 6 + (16 * images.Length);
        for (var i = 0; i < images.Length; i++)
        {
            var size = sizes[i];

            // 0 means 256 in this byte; Jane never draws that large, but the encoding is the rule.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);

            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }
    }

    /// <summary>
    /// One icon image as a 32-bit BITMAPINFOHEADER DIB.
    /// </summary>
    /// <remarks>
    /// A classic DIB rather than an embedded PNG: PNG-compressed entries are only reliably
    /// decoded at 256 px and above, and Jane's icon is never drawn that large.
    /// </remarks>
    private static byte[] RenderDib(int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            Draw(context, size);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        // The 1-bit AND mask is required by the format even though the alpha channel already
        // carries transparency. All zeroes means "take the colour data as-is".
        var maskStride = (size + 31) / 32 * 4;
        var maskLength = maskStride * size;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2); // XOR and AND planes stacked, as the format demands.
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixels.Length + maskLength);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // DIB rows run bottom-up.
        for (var y = size - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * stride, stride);
        }

        writer.Write(new byte[maskLength]);
        writer.Flush();

        return stream.ToArray();
    }

    private static void Draw(DrawingContext context, int size)
    {
        double edge = size;
        var corner = edge * 0.28;

        context.DrawRoundedRectangle(
            JanePalette.AccentBrush,
            null,
            new Rect(0, 0, edge, edge),
            corner,
            corner);

        var small = size < SmallSizeThreshold;
        var bars = small ? SmallBars : Bars;
        var barWidth = edge * (small ? 0.15 : 0.09);
        var gap = edge * (small ? 0.11 : 0.075);
        var span = (bars.Length * barWidth) + ((bars.Length - 1) * gap);
        var x = (edge - span) / 2;
        var tallest = edge * 0.52;

        foreach (var scale in bars)
        {
            var height = tallest * scale;

            context.DrawRoundedRectangle(
                Brushes.White,
                null,
                new Rect(x, (edge - height) / 2, barWidth, height),
                barWidth / 2,
                barWidth / 2);

            x += barWidth + gap;
        }
    }
}
