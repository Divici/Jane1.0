using System.Windows.Controls;

namespace Jane.App.Settings;

/// <summary>
/// The attribution surface CC-BY-4.0 requires, mirrored from <c>NOTICE.md</c>.
/// </summary>
/// <remarks>
/// Its own control rather than a section of the settings window, because it is the one part of
/// that window whose contents are a legal obligation rather than a design choice, and because
/// <c>SettingsWindowTests</c> renders it in isolation to assert that every entry appears.
/// </remarks>
public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();
}
