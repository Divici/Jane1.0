using System.Windows;
using System.Windows.Controls;
using Jane.App.Controls;
using Jane.Core.Abstractions;
using Jane.Core.Instructions;
using Jane.Core.Platform;
using Jane.Core.Storage;
using Jane.Core.Vocabulary;

namespace Jane.App.Settings;

/// <summary>The panes of the settings window, in navigation order.</summary>
public enum SettingsSection
{
    Dictation,
    Audio,
    Engine,
    Models,
    Formatting,
    DeepContext,
    Vocabulary,
    Instructions,
    Appearance,
    About,
}

/// <summary>
/// Jane's settings: a left navigation rail over ten panes, in the same dark palette as the pill.
/// </summary>
/// <remarks>
/// <para>
/// A left rail rather than a tab strip because there are ten sections and several of them need a
/// paragraph of explanation, which a strip has nowhere to put. It is a <see cref="TabControl"/>
/// underneath, restyled: selection, arrow-key navigation and automation peers all come free that
/// way, and re-implementing them over a list box is how a settings window loses its keyboard
/// support.
/// </para>
/// <para>
/// The window owns no state. Every control binds to <see cref="SettingsViewModel"/>, whose setters
/// write straight to SQLite -- so there is no Save button, no dirty flag, and no way to close
/// this window and lose something you typed.
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// The constructor the composition root uses.
    /// </summary>
    /// <param name="settings">Written on every change; no Save button exists.</param>
    /// <param name="provisioner">Downloads weights and pulls Ollama models. Stubbed in tests.</param>
    /// <param name="blocklist">
    /// The live instance, so the Deep Context pane can show what has been auto-blocked in this
    /// session as well as the fixed lists.
    /// </param>
    /// <param name="overlayVisibilityChanged">
    /// Called when the floating-bar toggle changes, so the pill can be hidden now rather than at
    /// the next restart. The window is deliberately not given the presenter itself.
    /// </param>
    /// <param name="database">
    /// The live database, read only for what About shows: where the file is and which migrations
    /// it has run. "Plaintext until you delete it" is a claim; a path somebody can open is what
    /// makes it checkable. Optional, and it falls back to the standard location.
    /// </param>
    public SettingsWindow(
        SettingsRepository settings,
        UserDictionary dictionary,
        CustomInstructions instructions,
        IModelProvisioner provisioner,
        IMicrophoneCatalog microphones,
        Blocklist blocklist,
        Action<bool>? overlayVisibilityChanged = null,
        JaneDatabase? database = null)
        : this(Build(settings, dictionary, instructions, provisioner, microphones, blocklist, overlayVisibilityChanged, database))
    {
    }

    private static SettingsViewModel Build(
        SettingsRepository settings,
        UserDictionary dictionary,
        CustomInstructions instructions,
        IModelProvisioner provisioner,
        IMicrophoneCatalog microphones,
        Blocklist blocklist,
        Action<bool>? overlayVisibilityChanged,
        JaneDatabase? database) =>
        new(settings, dictionary, instructions, provisioner, microphones, blocklist, overlayVisibilityChanged)
        {
            DatabaseLocation = database?.Path ?? new JanePaths().Database,
            SchemaDescription = Describe(database),
        };

    private static string Describe(JaneDatabase? database)
    {
        if (database is null)
        {
            return string.Empty;
        }

        var megabytes = database.FileSizeBytes / (1024.0 * 1024.0);
        var latest = database.AppliedMigrations.OrderBy(m => m.Version).LastOrDefault();

        return string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"Schema version {database.SchemaVersion} ({latest?.Name ?? "none"}), {megabytes:0.0} MB on disk.");
    }

    public SettingsWindow(SettingsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = model;
        InitializeComponent();

        DataContext = model;
        JaneChrome.Apply(this);
    }

    public SettingsViewModel Model { get; }

    /// <summary>The header bar, which is outside every section. Walked separately by the a11y test.</summary>
    public FrameworkElement Chrome => ChromeBar;

    /// <summary>Transient messages. Public so the composition root can report into an open window.</summary>
    public ToastHost Toasts => ToastLayer;

    public SettingsSection CurrentSection => TagOf(Nav.SelectedItem as TabItem) ?? SettingsSection.Dictation;

    /// <summary>Selects a pane. Used by the tray when it opens settings at a particular place.</summary>
    public void Show(SettingsSection section) => Nav.SelectedItem = ItemFor(section);

    /// <summary>The pane's root element, whether or not it has ever been on screen.</summary>
    public FrameworkElement SectionView(SettingsSection section) => (FrameworkElement)ItemFor(section).Content;

    public string TitleOf(SettingsSection section) => (string)ItemFor(section).Header;

    private TabItem ItemFor(SettingsSection section) =>
        Nav.Items.OfType<TabItem>().First(item => TagOf(item) == section);

    private static SettingsSection? TagOf(TabItem? item) => item?.Tag as SettingsSection?;

    // ----------------------------------------------------------------- handlers

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Nav))
        {
            // A combo box inside a pane also raises SelectionChanged, and it bubbles.
            return;
        }

        if (CurrentSection == SettingsSection.Models)
        {
            // Asking Ollama what it has is a network round trip to localhost, so it happens when
            // the pane is opened rather than when the window is constructed.
            await Model.RefreshModelsAsync(CancellationToken.None);
        }
    }

    private void OnHotkeyCaptured(object? sender, HotkeyBinding binding)
    {
        if (!Model.TryRebind(binding))
        {
            Toasts.Show("That key cannot be used", Severity.Error, Model.HotkeyStatus.Message);
        }
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e) => Model.TryRebind(HotkeyBinding.Default);

    private void OnRefreshMicrophones(object sender, RoutedEventArgs e)
    {
        Model.RefreshMicrophones();
        Toasts.Show($"{Model.Microphones.Count - 1} microphone(s) found", Severity.Info);
    }

    private void OnProbeBlocklist(object sender, RoutedEventArgs e) => Model.DeepContext.Probe();

    private async void OnAddDictionaryEntry(object sender, RoutedEventArgs e)
    {
        var term = Model.Dictionary.DraftTerm;
        await Model.Dictionary.AddAsync(CancellationToken.None);

        if (Model.Dictionary.Error is null)
        {
            Toasts.Show($"\"{term}\" added to your dictionary", Severity.Success);
        }
    }

    private async void OnToggleDictionaryEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DictionaryEntry entry } and CheckBox box)
        {
            await Model.Dictionary.SetEnabledAsync(entry, box.IsChecked == true, CancellationToken.None);
        }
    }

    private void OnEditDictionaryEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DictionaryEntry entry })
        {
            Model.Dictionary.Edit(entry);
        }
    }

    private async void OnRemoveDictionaryEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DictionaryEntry entry })
        {
            await Model.Dictionary.RemoveAsync(entry, CancellationToken.None);
            Toasts.Show($"\"{entry.Term}\" removed", Severity.Info);
        }
    }

    private async void OnSaveGlobalInstructions(object sender, RoutedEventArgs e)
    {
        await Model.Instructions.SaveGlobalAsync(CancellationToken.None);

        Toasts.Show(
            Model.Instructions.Error is null ? "Instructions saved" : "Could not save",
            Model.Instructions.Error is null ? Severity.Success : Severity.Error,
            Model.Instructions.Error);
    }

    private async void OnAddAppRule(object sender, RoutedEventArgs e)
    {
        var process = Model.Instructions.DraftProcess;
        await Model.Instructions.AddAppRuleAsync(CancellationToken.None);

        if (Model.Instructions.Error is null)
        {
            Toasts.Show($"Rules saved for {process}", Severity.Success);
        }
    }

    private async void OnToggleAppRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstructionSet rule } and CheckBox box)
        {
            await Model.Instructions.SetEnabledAsync(rule, box.IsChecked == true, CancellationToken.None);
        }
    }

    private void OnEditAppRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstructionSet rule })
        {
            Model.Instructions.Edit(rule);
        }
    }

    private async void OnRemoveAppRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstructionSet rule })
        {
            await Model.Instructions.RemoveAsync(rule, CancellationToken.None);
            Toasts.Show($"Rules for {rule.ProcessName} removed", Severity.Info);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Model.Dispose();
        base.OnClosed(e);
    }
}
