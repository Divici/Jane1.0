using System.Windows;
using System.Windows.Media;

namespace Jane.App.Controls;

/// <summary>
/// The colours Jane's windows are built from, and the one place they are defined.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Overlay.JanePalette"/> owns the pill's colours: a self-contained capsule that has
/// to read over somebody else's white document. These are the surfaces behind it -- window,
/// card, input, border, four weights of text -- and they only ever appear inside Jane's own
/// windows, which are dark by construction rather than by theme.
/// </para>
/// <para>
/// The state colours are deliberately re-exported from <c>JanePalette</c> rather than redefined.
/// The accent in the settings window and the accent in the pill being the same blue is the whole
/// point of having one identity colour.
/// </para>
/// </remarks>
public static class JaneTheme
{
    /// <summary>The page. Matches the darker end of the pill's gradient.</summary>
    public static Color WindowBackgroundColor { get; } = Color.FromRgb(0x0E, 0x10, 0x16);

    /// <summary>The left navigation rail, a shade darker than the page it sits beside.</summary>
    public static Color NavBackgroundColor { get; } = Color.FromRgb(0x0A, 0x0C, 0x11);

    /// <summary>A card: one group of related settings.</summary>
    public static Color SurfaceColor { get; } = Color.FromRgb(0x16, 0x19, 0x21);

    /// <summary>A control sitting on a card -- text box, combo box, button.</summary>
    public static Color InputColor { get; } = Color.FromRgb(0x1C, 0x20, 0x2A);

    public static Color InputHoverColor { get; } = Color.FromRgb(0x23, 0x28, 0x34);

    public static Color BorderColor { get; } = Color.FromRgb(0x26, 0x2B, 0x38);

    public static Color BorderStrongColor { get; } = Color.FromRgb(0x37, 0x3E, 0x4F);

    public static Color TextPrimaryColor { get; } = Color.FromRgb(0xEC, 0xEF, 0xF5);

    /// <summary>Descriptions under a label. Readable, but not competing with the label.</summary>
    public static Color TextSecondaryColor { get; } = Color.FromRgb(0xA3, 0xAC, 0xBE);

    /// <summary>Metadata: timestamps, sizes, licences.</summary>
    public static Color TextMutedColor { get; } = Color.FromRgb(0x77, 0x81, 0x94);

    public static SolidColorBrush WindowBackground { get; } = Frozen(WindowBackgroundColor);

    public static SolidColorBrush NavBackground { get; } = Frozen(NavBackgroundColor);

    public static SolidColorBrush Surface { get; } = Frozen(SurfaceColor);

    public static SolidColorBrush Input { get; } = Frozen(InputColor);

    public static SolidColorBrush InputHover { get; } = Frozen(InputHoverColor);

    public static SolidColorBrush Border { get; } = Frozen(BorderColor);

    public static SolidColorBrush BorderStrong { get; } = Frozen(BorderStrongColor);

    public static SolidColorBrush TextPrimary { get; } = Frozen(TextPrimaryColor);

    public static SolidColorBrush TextSecondary { get; } = Frozen(TextSecondaryColor);

    public static SolidColorBrush TextMuted { get; } = Frozen(TextMutedColor);

    /// <summary>Jane's identity colour. The same blue the pill uses while listening.</summary>
    public static SolidColorBrush Accent { get; } = Overlay.JanePalette.AccentBrush;

    /// <summary>A 12% wash of the accent, for a selected navigation item.</summary>
    public static SolidColorBrush AccentWash { get; } = Frozen(
        Color.FromArgb(0x1F, Overlay.JanePalette.Accent.R, Overlay.JanePalette.Accent.G, Overlay.JanePalette.Accent.B));

    public static SolidColorBrush Success { get; } = Overlay.JanePalette.InjectingBrush;

    public static SolidColorBrush Caution { get; } = Overlay.JanePalette.CautionBrush;

    public static SolidColorBrush Danger { get; } = Overlay.JanePalette.ErrorBrush;

    public static SolidColorBrush SuccessWash { get; } = Wash(Overlay.JanePalette.Injecting);

    public static SolidColorBrush CautionWash { get; } = Wash(Overlay.JanePalette.Caution);

    public static SolidColorBrush DangerWash { get; } = Wash(Overlay.JanePalette.Error);

    public static SolidColorBrush AccentWashStrong { get; } = Wash(Overlay.JanePalette.Accent);

    /// <summary>Focus ring. Drawn outside the control so it never moves the layout.</summary>
    public static SolidColorBrush FocusRing { get; } = Frozen(
        Color.FromArgb(0xCC, Overlay.JanePalette.Accent.R, Overlay.JanePalette.Accent.G, Overlay.JanePalette.Accent.B));

    /// <summary>Jane's UI font stack, matching the pill.</summary>
    public static FontFamily UiFont { get; } = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>For transcripts and window titles, where alignment carries meaning.</summary>
    public static FontFamily MonoFont { get; } = new("Cascadia Mono, Consolas, Courier New");

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Wash(Color color) => Frozen(Color.FromArgb(0x24, color.R, color.G, color.B));

    /// <summary>
    /// Merges Jane's styles into a window's own resources.
    /// </summary>
    /// <remarks>
    /// Into the window rather than into <see cref="Application.Current"/> on purpose: the tests
    /// drive these windows on a bare dispatcher with no <c>Application</c> at all, because an
    /// Application is process-wide singleton state one test would be imposing on every other.
    /// A window that carries its own styles works in both places.
    /// </remarks>
    public static void ApplyTo(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Jane;component/Controls/Theme.xaml", UriKind.Relative),
        });
    }
}
