using System.Windows;
using System.Windows.Controls;

namespace Jane.App.Controls;

/// <summary>
/// One card of related settings: a heading, an optional sentence of context, and the rows.
/// </summary>
/// <remarks>
/// The grouping is the design. A settings window that is one flat list of checkboxes makes the
/// reader do the sorting, and Jane has enough surface -- hotkey, audio, two model stacks, a
/// privacy boundary -- that the sorting is most of the work.
/// </remarks>
public sealed class SettingsGroup : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsGroup), new PropertyMetadata(null));

    /// <summary>One sentence under the heading. Null hides the line entirely rather than reserving it.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>
/// One setting: what it is called, what it does, and the control that changes it.
/// </summary>
/// <remarks>
/// <see cref="Description"/> is not decoration. Several of Jane's settings are only meaningful
/// with a sentence of explanation -- "minimum hold" is a number until you are told it is what
/// stops a game bind firing a dictation -- and a tooltip is not an explanation a keyboard user
/// or a screen reader reaches by default.
/// </remarks>
public sealed class SettingRow : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty NoteSeverityProperty = DependencyProperty.Register(
        nameof(NoteSeverity), typeof(Severity), typeof(SettingRow), new PropertyMetadata(Severity.Info));

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Live feedback about the current value -- a conflict, a warning, a confirmation.</summary>
    public string? Note
    {
        get => (string?)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public Severity NoteSeverity
    {
        get => (Severity)GetValue(NoteSeverityProperty);
        set => SetValue(NoteSeverityProperty, value);
    }
}

/// <summary>How much attention a message deserves. Drives colour and nothing else.</summary>
public enum Severity
{
    Info,

    Success,

    /// <summary>A limitation the user should know about, not a failure. Amber, as in the pill.</summary>
    Warning,

    Error,
}

/// <summary>A small pill of metadata beside a heading -- a licence, a mode, a bypass decision.</summary>
public sealed class Badge : ContentControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(Severity), typeof(Badge), new PropertyMetadata(Severity.Info));

    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }
}

/// <summary>
/// The designed state a pane shows when it has nothing, is fetching something, or failed.
/// </summary>
/// <remarks>
/// Every list in Jane's windows has one. "No blank panes" is a stated requirement of this phase,
/// and the honest way to meet it is a single control that cannot be forgotten rather than three
/// ad-hoc panels per list.
/// </remarks>
public sealed class StatePanel : Control
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatePanel), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StatePanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(StatePanel), new PropertyMetadata(null));

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(Severity), typeof(StatePanel), new PropertyMetadata(Severity.Info));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(StatePanel), new PropertyMetadata(false));

    /// <summary>A Segoe Fluent Icons code point. Purely decorative; the title carries the meaning.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>Swaps the glyph for an indeterminate bar. The loading state, without a second control.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
}
