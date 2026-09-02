using System.Windows;
using System.Windows.Media;

namespace Jane.App.Overlay;

/// <summary>
/// Every colour Jane paints, in one place.
/// </summary>
/// <remarks>
/// The pill is a dark, self-contained capsule rather than a themed surface, and deliberately so:
/// it floats over whatever the user is working in, so it cannot inherit a theme and has to read
/// on a white document and a black terminal alike. A near-opaque dark fill, a one-pixel light
/// rim and a soft shadow give it a hard edge against both. Every brush is frozen so the XAML and
/// the tray icon can share one instance across threads without a copy.
/// </remarks>
public static class JanePalette
{
    /// <summary>Listening, and Jane's identity colour -- the tray icon uses it too.</summary>
    public static Color Accent { get; } = Color.FromRgb(0x4C, 0x8D, 0xFF);

    /// <summary>Transcribing or formatting. Distinct from Accent so the pill reads at a glance.</summary>
    public static Color Thinking { get; } = Color.FromRgb(0xA7, 0x8B, 0xFA);

    /// <summary>Text is going into the target window.</summary>
    public static Color Injecting { get; } = Color.FromRgb(0x34, 0xD3, 0x99);

    /// <summary>Edit Mode unavailable -- a limitation, not a failure, so amber rather than red.</summary>
    public static Color Caution { get; } = Color.FromRgb(0xFB, 0xBF, 0x24);

    public static Color Error { get; } = Color.FromRgb(0xF8, 0x71, 0x71);

    public static SolidColorBrush AccentBrush { get; } = Frozen(Accent);

    public static SolidColorBrush ThinkingBrush { get; } = Frozen(Thinking);

    public static SolidColorBrush InjectingBrush { get; } = Frozen(Injecting);

    public static SolidColorBrush CautionBrush { get; } = Frozen(Caution);

    public static SolidColorBrush ErrorBrush { get; } = Frozen(Error);

    /// <summary>Pill fill. Alpha 0xF2 rather than opaque so the desktop shows faintly through.</summary>
    public static LinearGradientBrush PillBackground { get; } = FrozenGradient(
        Color.FromArgb(0xF2, 0x16, 0x19, 0x21),
        Color.FromArgb(0xF2, 0x0E, 0x10, 0x16));

    /// <summary>The rim that keeps the pill's edge visible against a dark desktop.</summary>
    public static SolidColorBrush PillBorder { get; } = Frozen(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));

    public static SolidColorBrush PillText { get; } = Frozen(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));

    /// <summary>Waveform bars at rest -- the flat line silence draws.</summary>
    public static SolidColorBrush WaveformIdle { get; } = Frozen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FrozenGradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
