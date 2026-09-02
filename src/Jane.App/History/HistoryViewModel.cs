using System.Collections.ObjectModel;
using System.Globalization;
using Jane.App.Controls;
using Jane.Core.Abstractions;
using Jane.Core.History;

namespace Jane.App.History;

/// <summary>
/// Everything the history window shows and every action it offers.
/// </summary>
/// <remarks>
/// <para>
/// The retention statement is a property of this class rather than a string in the XAML because
/// it is a requirement of the plan, not a caption: <em>the UI states plainly</em> that history is
/// plaintext, kept until deleted, and includes Deep Context captures. A test asserts those words
/// are on screen.
/// </para>
/// <para>
/// Re-injection is the one operation here that can go wrong in a way that matters. The entry
/// records the window it came from, and Windows recycles handles, so an entry aimed at a
/// long-closed editor could otherwise type into whatever inherited its HWND. Every path through
/// <see cref="Reinject"/> that is not an exact identity match reports and injects nothing.
/// </para>
/// </remarks>
public sealed class HistoryViewModel : ObservableObject
{
    /// <summary>How many rows the window holds. Past this, search is the right tool, not scrolling.</summary>
    private const int PageSize = 500;

    private readonly HistoryStore _history;
    private readonly Func<TargetWindow> _liveWindow;
    private readonly Action<HistoryEntry> _inject;
    private readonly Action<string> _copy;
    private Action<ToastMessage> _report;

    private string _search = string.Empty;
    private string? _application;
    private DictationMode? _mode;
    private bool? _bypassed;
    private bool _loading;
    private string? _error;
    private int _totalCount;

    /// <param name="liveWindow">What has focus right now. Re-inject is checked against it.</param>
    /// <param name="inject">
    /// Performs the actual injection. Injected rather than resolved so this class never needs the
    /// Win32 injector, and so the tests can assert that nothing was typed.
    /// </param>
    /// <param name="report">Where refusals and confirmations go -- the window's toast host.</param>
    public HistoryViewModel(
        HistoryStore history,
        Func<TargetWindow> liveWindow,
        Action<HistoryEntry> inject,
        Action<string> copy,
        Action<ToastMessage> report)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(liveWindow);
        ArgumentNullException.ThrowIfNull(inject);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(report);

        _history = history;
        _liveWindow = liveWindow;
        _inject = inject;
        _copy = copy;
        _report = report;
    }

    /// <summary>
    /// Redirects refusals and confirmations, once the window that owns them exists.
    /// </summary>
    /// <remarks>
    /// The window cannot pass its own toast host to this constructor, because the host is created
    /// by <c>InitializeComponent</c> and the view model has to exist before that runs. Rebinding
    /// afterwards is the smaller of the two evils; the alternative is a view model that reaches
    /// into a window.
    /// </remarks>
    public void ReportTo(Action<ToastMessage> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    /// <summary>Every application that appears in the loaded rows, for the filter.</summary>
    public ObservableCollection<string> Applications { get; } = [];

    /// <summary>
    /// The sentence the window opens with. Not a tooltip, and not in a document somewhere.
    /// </summary>
    public static string RetentionNotice =>
        "Everything you have ever dictated is stored here in plaintext, on this machine, and kept until you delete it -- including any on-screen text Deep Context read. Audio is never written to disk.";

    /// <summary>Where the file is, so the sentence above is checkable rather than merely stated.</summary>
    public string DatabaseLocation { get; set; } = string.Empty;

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Raise(nameof(IsFiltered));
            }
        }
    }

    /// <summary>Process name to filter on, or null for all of them.</summary>
    public string? Application
    {
        get => _application;
        set
        {
            if (Set(ref _application, value))
            {
                Raise(nameof(IsFiltered));
            }
        }
    }

    public DictationMode? Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(IsFiltered));
            }
        }
    }

    /// <summary>Null means both; true means only the dictations that skipped the language model.</summary>
    public bool? Bypassed
    {
        get => _bypassed;
        set
        {
            if (Set(ref _bypassed, value))
            {
                Raise(nameof(IsFiltered));
            }
        }
    }

    /// <summary>
    /// The mode and bypass filters as one list, because they are one question to a reader:
    /// "show me everything / only edits / only the ones that skipped cleanup".
    /// </summary>
    public IReadOnlyList<HistoryFilterChoice> Filters { get; } =
    [
        new("Everything", null, null),
        new("Dictation only", DictationMode.Dictation, null),
        new("Edit Mode only", DictationMode.Edit, null),
        new("Cleaned up by the model", null, false),
        new("Raw, model skipped", null, true),
    ];

    private HistoryFilterChoice? _filter;

    public HistoryFilterChoice Filter
    {
        get => _filter ?? Filters[0];
        set
        {
            if (value is null || !Set(ref _filter, value))
            {
                return;
            }

            Mode = value.Mode;
            Bypassed = value.Bypassed;
        }
    }

    private bool _confirmingErase;

    /// <summary>
    /// Whether the "erase everything" confirmation strip is showing.
    /// </summary>
    /// <remarks>
    /// An in-window confirmation rather than a modal dialog. A modal would be the obvious choice
    /// and it is the wrong one here: it cannot say how many entries are about to go without
    /// building a custom dialog anyway, and a blocking message box in a window the tests drive
    /// would hang the run rather than fail it.
    /// </remarks>
    public bool ConfirmingErase
    {
        get => _confirmingErase;
        set => Set(ref _confirmingErase, value);
    }

    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>The last read failure, in words. Drives the designed error state.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool IsEmpty => Rows.Count == 0 && !IsLoading && Error is null;

    /// <summary>Whether any filter is set, so the empty state can say which empty this is.</summary>
    public bool IsFiltered =>
        !string.IsNullOrWhiteSpace(Search) || Application is not null || Mode is not null || Bypassed is not null;

    public string EmptyTitle => IsFiltered ? "No dictations match" : "Nothing dictated yet";

    public string EmptyDescription => IsFiltered
        ? "Clear the search box and the filters to see everything Jane has recorded."
        : "Hold your push-to-talk key anywhere in Windows and speak. Every dictation lands here, with the application it went into and where the time went.";

    /// <summary>Total rows in the database, which is not the same as the number loaded.</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (Set(ref _totalCount, value))
            {
                Raise(nameof(CountDescription));
            }
        }
    }

    public string CountDescription => TotalCount switch
    {
        0 => "No dictations stored",
        1 => "1 dictation stored",
        _ => string.Create(CultureInfo.CurrentCulture, $"{TotalCount} dictations stored"),
    };

    /// <summary>Re-reads the list under the current filters.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        Raise(nameof(IsEmpty));

        try
        {
            var query = new HistoryQuery
            {
                Text = string.IsNullOrWhiteSpace(Search) ? null : Search,
                ProcessName = Application,
                Mode = Mode,
                Bypassed = Bypassed,
                Limit = PageSize,
            };

            var rows = await _history.SearchAsync(query, cancellationToken);
            var total = await _history.CountAsync(cancellationToken);

            Rows.Clear();
            foreach (var entry in rows)
            {
                Rows.Add(new HistoryRow(entry));
            }

            TotalCount = total;
            RefreshApplications();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            Rows.Clear();
            Error = "Jane could not read its history database. " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(IsEmpty));
            Raise(nameof(EmptyTitle));
            Raise(nameof(EmptyDescription));
        }
    }

    public async Task DeleteAsync(HistoryRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            await _history.DeleteAsync(row.Id, cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _report(new ToastMessage("Could not delete that entry", Severity.Error, ex.Message));
            return;
        }

        Rows.Remove(row);
        TotalCount = Math.Max(0, TotalCount - 1);
        Raise(nameof(IsEmpty));
    }

    /// <summary>
    /// Deletes every entry and compacts the file.
    /// </summary>
    /// <remarks>
    /// The compaction is the difference between a label and a promise: SQLite reuses freed pages
    /// but never shrinks the file, so without the vacuum <see cref="HistoryStore.DeleteAllAsync"/>
    /// performs, a deleted history would still be exactly as large -- and as readable -- as the
    /// words it used to hold.
    /// </remarks>
    public async Task DeleteEverythingAsync(CancellationToken cancellationToken)
    {
        int deleted;

        try
        {
            deleted = await _history.DeleteAllAsync(cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _report(new ToastMessage("Could not erase history", Severity.Error, ex.Message));
            return;
        }

        ConfirmingErase = false;
        Search = string.Empty;
        Application = null;
        Filter = Filters[0];

        Rows.Clear();
        Applications.Clear();
        TotalCount = 0;

        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyDescription));

        _report(new ToastMessage(
            deleted == 1 ? "1 dictation erased" : $"{deleted} dictations erased",
            Severity.Success,
            "The database was rebuilt afterwards, so the deleted text is gone from the file rather than merely unlinked."));
    }

    public void Copy(HistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            _copy(row.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another process can hold the clipboard open; that is somebody else's bug, but it is
            // this window's job to say so rather than to look like it did nothing.
            _report(new ToastMessage("Could not reach the clipboard", Severity.Error, ex.Message));
            return;
        }

        _report(new ToastMessage("Copied", Severity.Success));
    }

    /// <summary>
    /// Types an entry into the window it originally came from, if that window is still there.
    /// </summary>
    /// <remarks>
    /// Every refusal path reports and injects nothing. The identity check is the whole point:
    /// handle, process id and process name must all match, because a handle alone is not identity
    /// once Windows has recycled it.
    /// </remarks>
    public void Reinject(HistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var live = _liveWindow();
        var check = row.Entry.CheckReinject(live);

        if (check != ReinjectCheck.Ready)
        {
            _report(Refusal(check, row, live));
            return;
        }

        _inject(row.Entry);
    }

    /// <summary>The designed message for each way a re-inject can be refused.</summary>
    public static ToastMessage Refusal(ReinjectCheck check, HistoryRow row, TargetWindow live) => check switch
    {
        ReinjectCheck.NoRecordedTarget => new ToastMessage(
            "Nothing to type into",
            Severity.Warning,
            "This entry has no window recorded against it -- it was an aborted dictation, so there is nowhere to put it back. Copy it instead."),

        ReinjectCheck.WindowGone => new ToastMessage(
            "That window is no longer open",
            Severity.Warning,
            $"This was dictated into {row.Application} ({row.WindowTitle}), and nothing has focus now. Nothing was typed. Focus that window and try again, or copy the text."),

        ReinjectCheck.DifferentWindow => new ToastMessage(
            "A different window has focus",
            Severity.Warning,
            $"This was dictated into {row.Application}, and {Describe(live)} is in front now. Jane will not type it somewhere it did not come from. Nothing was typed."),

        _ => new ToastMessage("Nothing was typed", Severity.Warning),
    };

    private static string Describe(TargetWindow live) =>
        live.IsNone ? "nothing" : string.IsNullOrWhiteSpace(live.ProcessName) ? "another window" : live.ProcessName;

    private void RefreshApplications()
    {
        // Only rebuilt from an unfiltered read. Deriving the list from filtered rows would leave
        // the application filter holding exactly one option -- the one already chosen.
        if (Application is not null)
        {
            return;
        }

        var names = Rows
            .Select(r => r.Entry.Target.ProcessName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Applications.Clear();
        foreach (var name in names)
        {
            Applications.Add(name);
        }
    }
}

/// <param name="Mode">Null means both modes.</param>
/// <param name="Bypassed">Null means both bypass outcomes.</param>
public sealed record HistoryFilterChoice(string Name, DictationMode? Mode, bool? Bypassed)
{
    public override string ToString() => Name;
}
