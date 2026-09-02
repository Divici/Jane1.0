using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jane.App.Controls;
using Jane.Core.Abstractions;
using Jane.Core.History;
using Jane.Core.Platform;
using Jane.Core.Storage;

namespace Jane.App.History;

/// <summary>
/// Everything Jane has ever dictated, searchable, with the application and the time beside each
/// entry -- and a banner saying plainly what is being kept and for how long.
/// </summary>
/// <remarks>
/// <para>
/// The one operation that has to be careful is "type again". The entry knows the window it came
/// from, but a window handle is not an identity once Windows has recycled it, so the check is
/// handle plus process id plus process name, and every outcome that is not an exact match reports
/// and types nothing at all.
/// </para>
/// <para>
/// The window takes the injection as a delegate rather than an <c>ITextInjector</c>. A history
/// window that held the injector could type anywhere; one that holds a function can only do what
/// the composition root decided it may.
/// </para>
/// </remarks>
public partial class HistoryWindow : Window
{
    /// <param name="liveWindow">What has focus right now, for the re-inject identity check.</param>
    /// <param name="inject">Performs the injection. Called only on an exact identity match.</param>
    /// <param name="copyToClipboard">
    /// Defaults to the WPF clipboard. Injected so the tests do not contend for a process-wide,
    /// machine-wide resource that another application can hold open.
    /// </param>
    /// <param name="database">
    /// The live database, read only for its path. The window says history is plaintext and kept
    /// until deleted; showing the file it is kept in is what turns that from a claim into
    /// something the reader can go and verify. Optional, and it falls back to the standard
    /// location, which is where the shipped app keeps it.
    /// </param>
    public HistoryWindow(
        HistoryStore history,
        Func<TargetWindow> liveWindow,
        Action<HistoryEntry> inject,
        Action<string>? copyToClipboard = null,
        JaneDatabase? database = null)
        : this(Build(history, liveWindow, inject, copyToClipboard, database))
    {
    }

    private static HistoryViewModel Build(
        HistoryStore history,
        Func<TargetWindow> liveWindow,
        Action<HistoryEntry> inject,
        Action<string>? copyToClipboard,
        JaneDatabase? database) =>
        new(history,
            liveWindow,
            inject,
            copyToClipboard ?? (text => Clipboard.SetText(text)),
            _ => { })
        {
            DatabaseLocation = database?.Path ?? new JanePaths().Database,
        };

    public HistoryWindow(HistoryViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = model;
        InitializeComponent();

        DataContext = model;
        JaneChrome.Apply(this);

        // The view model reports through whatever it was handed; the window's toast host only
        // exists once InitializeComponent has run, so the report target is rebound here.
        Model.ReportTo(ToastLayer.Show);

        Loaded += async (_, _) => await Model.RefreshAsync(CancellationToken.None);
    }

    public HistoryViewModel Model { get; }

    /// <summary>The whole page, for the tests that walk it.</summary>
    public FrameworkElement Page => Root;

    public ToastHost Toasts => ToastLayer;

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await Model.RefreshAsync(CancellationToken.None);

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await Model.RefreshAsync(CancellationToken.None);
        }
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        // Enter searches, rather than every keystroke firing a query at SQLite. The list is
        // small enough that either would work; typing that queries on every character is how a
        // search box starts feeling slow on the one machine with a large history.
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await Model.RefreshAsync(CancellationToken.None);
        }
    }

    private async void OnClearFilters(object sender, RoutedEventArgs e)
    {
        Model.Search = string.Empty;
        Model.Application = null;
        Model.Filter = Model.Filters[0];

        await Model.RefreshAsync(CancellationToken.None);
    }

    private void OnAskToErase(object sender, RoutedEventArgs e) => Model.ConfirmingErase = true;

    private void OnCancelErase(object sender, RoutedEventArgs e) => Model.ConfirmingErase = false;

    private async void OnConfirmErase(object sender, RoutedEventArgs e) =>
        await Model.DeleteEverythingAsync(CancellationToken.None);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
        {
            Model.Copy(row);
        }
    }

    private void OnReinject(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
        {
            Model.Reinject(row);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
        {
            await Model.DeleteAsync(row, CancellationToken.None);
        }
    }
}
