using System.Text;
using Jane.Core.Abstractions;
using Jane.Core.Formatting;
using Jane.Core.Instructions;
using Jane.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Jane.Core.Vocabulary;

/// <summary>
/// One term Jane has been taught.
/// </summary>
/// <remarks>
/// The three fields do three different jobs, and an entry may use any combination of them:
/// <list type="bullet">
/// <item><description>
/// <paramref name="Term"/> is what the user cares about -- "Kubernetes", "Aihe", "k8s". It is fed
/// to sherpa-onnx as a contextual-biasing hotword and named in the LLM prompt.
/// </description></item>
/// <item><description>
/// <paramref name="PronunciationHint"/> is what the recogniser is likely to emit instead --
/// "koober netties". Biasing toward it as well catches the word either way, and it counts as a
/// trigger for the replacement so the mangled form still gets corrected.
/// </description></item>
/// <item><description>
/// <paramref name="Replacement"/> is what should be written when the term is heard -- term
/// "k eight s", replacement "k8s". This is the field that takes the Phase 7 bypass away, because
/// a bypassed dictation would silently skip it.
/// </description></item>
/// </list>
/// </remarks>
public sealed record DictionaryEntry(
    long Id,
    string Term,
    string? PronunciationHint,
    string? Replacement,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool HasReplacement => !string.IsNullOrWhiteSpace(Replacement);

    public bool HasPronunciationHint => !string.IsNullOrWhiteSpace(PronunciationHint);
}

/// <summary>An entry before it has been saved. Used for bulk import from onboarding or a paste.</summary>
public sealed record DictionaryDraft(
    string Term,
    string? PronunciationHint = null,
    string? Replacement = null,
    bool Enabled = true);

/// <summary>
/// The read side of the dictionary -- everything the pipeline and the Phase 7 bypass consume.
/// </summary>
/// <remarks>
/// Separate from the concrete store so <c>BypassHeuristic</c> and <c>PromptBuilder</c> can be
/// tested without a database, and so the in-game route can be handed an empty implementation.
/// </remarks>
public interface IUserDictionary
{
    /// <summary>Every entry, enabled or not, ordered by term. What the settings window lists.</summary>
    IReadOnlyList<DictionaryEntry> Entries { get; }

    /// <summary>
    /// The terms of enabled entries, for the prompt's vocabulary block.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same list as <see cref="Hotwords"/>. A pronunciation hint is the
    /// mangled form the recogniser produces, so biasing toward it helps; putting it in a list
    /// headed "spell these correctly" would teach the model the mistake.
    /// </remarks>
    IReadOnlyList<string> Terms { get; }

    /// <summary>
    /// Terms and pronunciation hints of enabled entries, for sherpa-onnx contextual biasing.
    /// </summary>
    /// <remarks>
    /// Goes straight into <c>RecognitionOptions.Hotwords</c>. Biasing forces
    /// <c>modified_beam_search</c> in place of greedy decoding; P1-6 measured that at 88% of
    /// greedy's p50 on this machine, so it is enabled rather than prompt-side only.
    /// </remarks>
    IReadOnlyList<string> Hotwords { get; }

    /// <summary>
    /// Whether any enabled replacement applies to this transcript.
    /// </summary>
    /// <remarks>
    /// This is the Phase 7 bypass hook. A pending replacement means the LLM has work to do, so
    /// the bypass must not fire -- BLOCKER #9 is precisely the case where it did and the user's
    /// own dictionary was skipped without a word.
    /// </remarks>
    bool HasPendingReplacement(string transcript);

    /// <summary>The entries behind <see cref="HasPendingReplacement"/>, for the prompt and the log.</summary>
    IReadOnlyList<DictionaryEntry> PendingReplacementsFor(string transcript);

    /// <summary>
    /// Applies every pending replacement literally, without the LLM.
    /// </summary>
    /// <remarks>
    /// The in-game route skips the LLM entirely, so without this pass the user's replacements
    /// would simply never happen while a game is running. Matching is whole-word and
    /// case-insensitive; the replacement is inserted exactly as it was stored.
    /// </remarks>
    string ApplyReplacements(string text);

    /// <summary>The dictionary as prompt text. Empty when there is nothing to say.</summary>
    string PromptSection();
}

/// <summary>
/// The custom dictionary, backed by <c>jane.db</c> and cached in memory.
/// </summary>
/// <remarks>
/// <para>
/// Cached because <see cref="Hotwords"/> is read on every single dictation, at key-down, on the
/// path the user feels most. Writes are rare and go through the database first, then refresh the
/// snapshot, so the cache can never be ahead of the file.
/// </para>
/// <para>
/// There is no entry cap. Aqua stops at 800; that is a product decision on a cloud service, and
/// nothing in contextual biasing or in this schema needs it.
/// </para>
/// </remarks>
public sealed class UserDictionary : IUserDictionary
{
    private const string SelectAll =
        "SELECT id, term, pronunciation_hint, replacement, enabled, created_at, updated_at " +
        "FROM dictionary ORDER BY term COLLATE NOCASE;";

    private readonly JaneDatabase _database;

    private IReadOnlyList<DictionaryEntry> _entries = [];
    private IReadOnlyList<string> _terms = [];
    private IReadOnlyList<string> _hotwords = [];
    private IReadOnlyList<DictionaryEntry> _replacements = [];

    public UserDictionary(JaneDatabase database)
    {
        _database = database;
        Reload();
    }

    public IReadOnlyList<DictionaryEntry> Entries => _entries;

    public IReadOnlyList<string> Terms => _terms;

    public IReadOnlyList<string> Hotwords => _hotwords;

    /// <summary>Raised after any change, so an open settings window refreshes itself.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Adds a term, or edits the one that is already there.
    /// </summary>
    /// <remarks>
    /// Terms are unique case-insensitively. Adding "kubernetes" when "Kubernetes" exists edits
    /// that entry rather than creating a near-duplicate that would bias against itself.
    /// </remarks>
    public async Task<DictionaryEntry> AddAsync(
        string term,
        string? pronunciationHint,
        string? replacement,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var id = await _database.WriteAsync(
            (connection, transaction) => Upsert(
                connection,
                transaction,
                new DictionaryDraft(term.Trim(), Clean(pronunciationHint), Clean(replacement))),
            cancellationToken);

        Reload();
        return _entries.First(e => e.Id == id);
    }

    /// <summary>Imports many entries in one transaction. Onboarding and paste-a-list use this.</summary>
    public async Task AddRangeAsync(IReadOnlyList<DictionaryDraft> drafts, CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
        {
            return;
        }

        await _database.WriteAsync((connection, transaction) =>
        {
            foreach (var draft in drafts)
            {
                if (string.IsNullOrWhiteSpace(draft.Term))
                {
                    continue;
                }

                Upsert(connection, transaction, draft with
                {
                    Term = draft.Term.Trim(),
                    PronunciationHint = Clean(draft.PronunciationHint),
                    Replacement = Clean(draft.Replacement),
                });
            }

            return true;
        }, cancellationToken);

        Reload();
    }

    public async Task<DictionaryEntry?> UpdateAsync(
        long id,
        string term,
        string? pronunciationHint,
        string? replacement,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var changed = await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dictionary
                   SET term = $term,
                       pronunciation_hint = $hint,
                       replacement = $replacement,
                       enabled = $enabled,
                       updated_at = $now
                 WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$term", term.Trim());
            command.Parameters.AddWithValue("$hint", (object?)Clean(pronunciationHint) ?? DBNull.Value);
            command.Parameters.AddWithValue("$replacement", (object?)Clean(replacement) ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$now", Timestamps.ToText(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }, cancellationToken);

        if (!changed)
        {
            return null;
        }

        Reload();
        return _entries.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Turns an entry off without losing it. A disabled entry biases nothing.</summary>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken)
    {
        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE dictionary SET enabled = $enabled, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$now", Timestamps.ToText(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }, cancellationToken);

        Reload();
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken)
    {
        var removed = await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM dictionary WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }, cancellationToken);

        if (removed)
        {
            Reload();
        }

        return removed;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM dictionary;";
            return command.ExecuteNonQuery();
        }, cancellationToken);

        Reload();
    }

    public bool HasPendingReplacement(string transcript) =>
        _replacements.Count > 0 && FindPending(transcript).Count > 0;

    public IReadOnlyList<DictionaryEntry> PendingReplacementsFor(string transcript) => FindPending(transcript);

    public string ApplyReplacements(string text)
    {
        if (string.IsNullOrEmpty(text) || _replacements.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var entry in _replacements)
        {
            result = ReplaceWholeWords(result, entry.Term, entry.Replacement!);

            if (entry.HasPronunciationHint)
            {
                result = ReplaceWholeWords(result, entry.PronunciationHint!, entry.Replacement!);
            }
        }

        return result;
    }

    public string PromptSection()
    {
        var enabled = _entries.Where(e => e.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var entry in enabled)
        {
            builder.Append("- ").Append(entry.Term);

            if (entry.HasPronunciationHint)
            {
                builder.Append(" (may be transcribed as \"").Append(entry.PronunciationHint).Append("\")");
            }

            if (entry.HasReplacement)
            {
                builder.Append(" -> write \"").Append(entry.Replacement).Append('"');
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Refreshes the in-memory snapshot from the database.</summary>
    public void Reload()
    {
        var entries = _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectAll;
            using var reader = command.ExecuteReader();

            var rows = new List<DictionaryEntry>();
            while (reader.Read())
            {
                rows.Add(new DictionaryEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4) != 0,
                    Timestamps.FromText(reader.GetString(5)),
                    Timestamps.FromText(reader.GetString(6))));
            }

            return rows;
        });

        _entries = entries;

        var terms = new List<string>();
        var hotwords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.Where(e => e.Enabled))
        {
            if (seen.Add(entry.Term))
            {
                terms.Add(entry.Term);
                hotwords.Add(entry.Term);
            }

            if (entry.HasPronunciationHint && seen.Add(entry.PronunciationHint!))
            {
                hotwords.Add(entry.PronunciationHint!);
            }
        }

        _terms = terms;
        _hotwords = hotwords;
        _replacements = [.. entries.Where(e => e.Enabled && e.HasReplacement)];

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static long Upsert(SqliteConnection connection, SqliteTransaction transaction, DictionaryDraft draft)
    {
        var now = Timestamps.ToText(DateTimeOffset.UtcNow);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // ON CONFLICT on the case-insensitive term index, so re-adding a term edits it. The term
        // itself is overwritten too: the user retyped it, and their casing is the one they want.
        command.CommandText = """
            INSERT INTO dictionary (term, pronunciation_hint, replacement, enabled, created_at, updated_at)
            VALUES ($term, $hint, $replacement, $enabled, $now, $now)
            ON CONFLICT (term COLLATE NOCASE) DO UPDATE SET
                term = excluded.term,
                pronunciation_hint = excluded.pronunciation_hint,
                replacement = excluded.replacement,
                enabled = excluded.enabled,
                updated_at = excluded.updated_at
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$term", draft.Term);
        command.Parameters.AddWithValue("$hint", (object?)draft.PronunciationHint ?? DBNull.Value);
        command.Parameters.AddWithValue("$replacement", (object?)draft.Replacement ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", draft.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);

        return (long)command.ExecuteScalar()!;
    }

    private List<DictionaryEntry> FindPending(string transcript)
    {
        var pending = new List<DictionaryEntry>();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return pending;
        }

        foreach (var entry in _replacements)
        {
            if (ContainsWholeWord(transcript, entry.Term) ||
                (entry.HasPronunciationHint && ContainsWholeWord(transcript, entry.PronunciationHint!)))
            {
                pending.Add(entry);
            }
        }

        return pending;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Whole-word, case-insensitive containment.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than a regular expression because a dictionary term is arbitrary user
    /// text: "C++" or "$HOME" would either need escaping at every call site or would quietly
    /// compile into something that matches far more than the user meant. A boundary is anything
    /// that is not a letter, a digit or an apostrophe, so "don't" stays one word.
    /// </remarks>
    internal static bool ContainsWholeWord(string haystack, string needle) =>
        IndexOfWholeWord(haystack, needle, 0) >= 0;

    private static int IndexOfWholeWord(string haystack, string needle, int start)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return -1;
        }

        while (start <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return -1;
            }

            var before = at == 0 || !IsWordCharacter(haystack[at - 1]);
            var afterAt = at + needle.Length;
            var after = afterAt == haystack.Length || !IsWordCharacter(haystack[afterAt]);

            if (before && after)
            {
                return at;
            }

            start = at + 1;
        }

        return -1;
    }

    private static string ReplaceWholeWords(string text, string needle, string replacement)
    {
        var at = IndexOfWholeWord(text, needle, 0);
        if (at < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        while (at >= 0)
        {
            builder.Append(text, cursor, at - cursor).Append(replacement);
            cursor = at + needle.Length;
            at = IndexOfWholeWord(text, needle, cursor);
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Whether a character continues a word. Apostrophes count so "don't" and "O'Brien" are one
    /// word each; a term boundary against them would match halves of contractions.
    /// </summary>
    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '’';
}

/// <summary>
/// Resolves the user's dictionary and Custom Instructions into the policy Phase 7 consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is the one seam between what a person configured and what the formatter does with it.
/// <c>BypassHeuristic</c> asks it whether anything would be lost by skipping the LLM, and
/// <c>PromptBuilder</c> asks it what to put in the vocabulary and instructions blocks -- resolved
/// once, up front, so the two can never disagree about whether a replacement was pending.
/// </para>
/// <para>
/// It lives beside the dictionary rather than in <c>Jane.Core.Formatting</c> because it reads
/// storage and the formatter deliberately does not know storage exists.
/// </para>
/// </remarks>
public sealed class StoredFormattingPolicySource(IUserDictionary dictionary, ICustomInstructions instructions)
    : IFormattingPolicySource
{
    public FormattingPolicy Resolve(string transcript, FormattingContext context)
    {
        var combined = instructions.ResolveFor(context.TargetProcessName).Combined;
        var pending = dictionary.PendingReplacementsFor(transcript);

        return new FormattingPolicy
        {
            CustomInstructions = string.IsNullOrWhiteSpace(combined) ? null : combined,
            Replacements = [.. pending.Select(entry => new DictionaryReplacement(
                SpokenFormIn(transcript, entry), entry.Replacement!))],

            // Terms, not hotwords: the prompt block asks the model to spell these correctly, and
            // a pronunciation hint is the misspelling.
            Terms = dictionary.Terms,
        };
    }

    /// <summary>
    /// Which form of the entry the transcript actually contains.
    /// </summary>
    /// <remarks>
    /// One replacement per entry, never two, so the bypass's "N pending replacements" count is
    /// the number of things that would be lost rather than the number of ways they could match.
    /// The term wins when both forms appear.
    /// </remarks>
    private static string SpokenFormIn(string transcript, DictionaryEntry entry) =>
        !UserDictionary.ContainsWholeWord(transcript, entry.Term) && entry.HasPronunciationHint
            ? entry.PronunciationHint!
            : entry.Term;
}
