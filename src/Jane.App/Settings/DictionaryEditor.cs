using System.Collections.ObjectModel;
using Jane.App.Controls;
using Jane.Core.Vocabulary;

namespace Jane.App.Settings;

/// <summary>
/// The custom dictionary, as a list with a form on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Three fields, doing three different jobs, and the window has to make that legible: the
/// <em>term</em> is biased toward during recognition, the <em>pronunciation hint</em> is the
/// mangled form the recogniser tends to produce instead, and the <em>replacement</em> is what
/// gets written when either is heard. Only the term is required.
/// </para>
/// <para>
/// A replacement is the one that changes Jane's behaviour beyond biasing: a transcript with a
/// pending replacement in it may not take the Phase 7 bypass, because a bypassed dictation would
/// skip the user's own dictionary in silence. The row says so, rather than leaving it as an
/// invisible property of the pipeline.
/// </para>
/// </remarks>
public sealed class DictionaryEditor : ObservableObject
{
    private readonly UserDictionary _dictionary;
    private string _draftTerm = string.Empty;
    private string _draftPronunciation = string.Empty;
    private string _draftReplacement = string.Empty;
    private string _filter = string.Empty;
    private string? _error;

    public DictionaryEditor(UserDictionary dictionary)
    {
        _dictionary = dictionary;
        _dictionary.Changed += OnChanged;
        Refresh();
    }

    /// <summary>Every entry, filtered by <see cref="Filter"/>, ordered by term.</summary>
    public ObservableCollection<DictionaryEntry> Entries { get; } = [];

    public string DraftTerm
    {
        get => _draftTerm;
        set
        {
            if (Set(ref _draftTerm, value))
            {
                Error = null;
                Raise(nameof(CanAdd));
            }
        }
    }

    /// <summary>What the recogniser is likely to emit instead, e.g. "koober netties".</summary>
    public string DraftPronunciation
    {
        get => _draftPronunciation;
        set => Set(ref _draftPronunciation, value);
    }

    /// <summary>What should be written when the term is heard. Blank means "write the term".</summary>
    public string DraftReplacement
    {
        get => _draftReplacement;
        set => Set(ref _draftReplacement, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>The last failed save, in the user's words. Null when there is nothing wrong.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool CanAdd => !string.IsNullOrWhiteSpace(DraftTerm);

    /// <summary>True when the list is empty because nothing matches, rather than because it is empty.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Filter);

    /// <summary>How many entries hold a replacement, which is what suppresses the bypass.</summary>
    public int ReplacementCount => _dictionary.Entries.Count(e => e.Enabled && e.HasReplacement);

    public int TotalCount => _dictionary.Entries.Count;

    /// <summary>Adds the draft, or edits the existing entry when the term is already known.</summary>
    public async Task AddAsync(CancellationToken cancellationToken)
    {
        if (!CanAdd)
        {
            return;
        }

        try
        {
            await _dictionary.AddAsync(DraftTerm, DraftPronunciation, DraftReplacement, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = $"Could not save \"{DraftTerm}\": {ex.Message}";
            return;
        }

        DraftTerm = string.Empty;
        DraftPronunciation = string.Empty;
        DraftReplacement = string.Empty;
        Error = null;
    }

    /// <summary>Loads an entry back into the form, so editing is the same gesture as adding.</summary>
    public void Edit(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DraftTerm = entry.Term;
        DraftPronunciation = entry.PronunciationHint ?? string.Empty;
        DraftReplacement = entry.Replacement ?? string.Empty;
    }

    public Task SetEnabledAsync(DictionaryEntry entry, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _dictionary.SetEnabledAsync(entry.Id, enabled, cancellationToken);
    }

    public async Task RemoveAsync(DictionaryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _dictionary.RemoveAsync(entry.Id, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) => _dictionary.ClearAsync(cancellationToken);

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var matches = _dictionary.Entries.Where(Matches).ToList();

        Entries.Clear();
        foreach (var entry in matches)
        {
            Entries.Add(entry);
        }

        Raise(nameof(TotalCount));
        Raise(nameof(ReplacementCount));
        Raise(nameof(IsFiltered));
    }

    private bool Matches(DictionaryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(Filter))
        {
            return true;
        }

        var needle = Filter.Trim();
        return entry.Term.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               (entry.PronunciationHint?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Replacement?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public void Detach() => _dictionary.Changed -= OnChanged;
}
