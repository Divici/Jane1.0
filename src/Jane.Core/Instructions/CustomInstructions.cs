using Jane.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Jane.Core.Instructions;

/// <summary>
/// One block of free-text style rules -- either the global set or one app's override.
/// </summary>
/// <param name="ProcessName">
/// <c>null</c> is the global set. Anything else is a per-app override, keyed on the process name
/// exactly as <c>TargetWindow.ProcessName</c> reports it: lower-case, no extension.
/// </param>
public sealed record InstructionSet(
    long Id,
    string? ProcessName,
    string Text,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsGlobal => ProcessName is null;
}

/// <summary>
/// What actually applies to one dictation.
/// </summary>
/// <remarks>
/// Global and per-app rules compose rather than replace: a person who wrote "never use
/// exclamation marks" meant it in Slack too. The app's rules come last in
/// <see cref="Combined"/>, which is how an instruction-following model resolves a conflict --
/// later instructions win.
/// </remarks>
public sealed record ResolvedInstructions(string? Global, string? PerApp)
{
    public static ResolvedInstructions None { get; } = new(null, null);

    public bool IsEmpty => Global is null && PerApp is null;

    /// <summary>Both blocks, global first. Empty string when there is nothing to say.</summary>
    public string Combined
    {
        get
        {
            if (Global is null)
            {
                return PerApp ?? string.Empty;
            }

            return PerApp is null ? Global : $"{Global}\n\n{PerApp}";
        }
    }
}

/// <summary>
/// The read side of Custom Instructions -- what the prompt builder and the bypass consume.
/// </summary>
public interface ICustomInstructions
{
    /// <summary>Every set, enabled or not, global first. What the settings window lists.</summary>
    IReadOnlyList<InstructionSet> All { get; }

    /// <summary>The global rules, or null if there are none.</summary>
    string? Global { get; }

    ResolvedInstructions ResolveFor(string? processName);

    /// <summary>
    /// Whether any instruction is active for this app.
    /// </summary>
    /// <remarks>
    /// The Phase 7 bypass hook. An app with instructions must never be bypassed, or the rules the
    /// user wrote are skipped in silence. Global rules apply everywhere by definition, so a
    /// non-empty global block suppresses the bypass in every app -- which is the honest reading
    /// of "no Custom Instructions active for the focused app".
    /// </remarks>
    bool HasInstructionsFor(string? processName);
}

/// <summary>
/// Free-text style rules, globally and per app, backed by <c>jane.db</c> and cached in memory.
/// </summary>
/// <remarks>
/// The per-app table is a deliberate improvement on Aqua, which offers one natural-language
/// Custom Instructions box and asks the model to work out which app it is in. A table keyed on
/// process name makes "terse in the terminal, formal in Outlook" a fact rather than a hint, and
/// it is what lets the bypass know, before any model runs, that this app has rules.
/// </remarks>
public sealed class CustomInstructions : ICustomInstructions
{
    private const string SelectAll =
        "SELECT id, process_name, text, enabled, created_at, updated_at " +
        "FROM instructions ORDER BY (process_name IS NOT NULL), process_name COLLATE NOCASE;";

    private readonly JaneDatabase _database;

    private IReadOnlyList<InstructionSet> _all = [];
    private string? _global;
    private Dictionary<string, string> _perApp = new(StringComparer.OrdinalIgnoreCase);

    public CustomInstructions(JaneDatabase database)
    {
        _database = database;
        Reload();
    }

    public IReadOnlyList<InstructionSet> All => _all;

    public string? Global => _global;

    /// <summary>Raised after any change, so an open settings window refreshes itself.</summary>
    public event EventHandler? Changed;

    public ResolvedInstructions ResolveFor(string? processName)
    {
        var key = Normalise(processName);
        var perApp = key is not null && _perApp.TryGetValue(key, out var text) ? text : null;

        return _global is null && perApp is null
            ? ResolvedInstructions.None
            : new ResolvedInstructions(_global, perApp);
    }

    public bool HasInstructionsFor(string? processName) => !ResolveFor(processName).IsEmpty;

    /// <summary>Sets the global rules. Null or blank removes them.</summary>
    public Task<InstructionSet?> SetGlobalAsync(string? text, CancellationToken cancellationToken) =>
        SetAsync(null, text, cancellationToken);

    /// <summary>
    /// Sets one app's rules. Null or blank removes the override.
    /// </summary>
    /// <remarks>
    /// The process name is normalised the way <c>TargetWindow.ProcessName</c> arrives -- trimmed,
    /// lower-cased, <c>.exe</c> dropped -- because the settings window lets a person type it and
    /// "Code.exe" and "code" are the same app.
    /// </remarks>
    public Task<InstructionSet?> SetForAppAsync(string processName, string? text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        return SetAsync(Normalise(processName), text, cancellationToken);
    }

    /// <summary>Turns a set off without losing the text the user wrote.</summary>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken)
    {
        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE instructions SET enabled = $enabled, updated_at = $now WHERE id = $id;";
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
            command.CommandText = "DELETE FROM instructions WHERE id = $id;";
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
            command.CommandText = "DELETE FROM instructions;";
            return command.ExecuteNonQuery();
        }, cancellationToken);

        Reload();
    }

    /// <summary>Refreshes the in-memory snapshot from the database.</summary>
    public void Reload()
    {
        _all = _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectAll;
            using var reader = command.ExecuteReader();

            var rows = new List<InstructionSet>();
            while (reader.Read())
            {
                rows.Add(new InstructionSet(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    Timestamps.FromText(reader.GetString(4)),
                    Timestamps.FromText(reader.GetString(5))));
            }

            return rows;
        });

        _global = _all.FirstOrDefault(s => s.IsGlobal && s.Enabled)?.Text;
        _perApp = _all
            .Where(s => !s.IsGlobal && s.Enabled)
            .ToDictionary(s => s.ProcessName!, s => s.Text, StringComparer.OrdinalIgnoreCase);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<InstructionSet?> SetAsync(string? processName, string? text, CancellationToken cancellationToken)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        var id = await _database.WriteAsync(
            (connection, transaction) => trimmed is null
                ? Delete(connection, transaction, processName)
                : Upsert(connection, transaction, processName, trimmed),
            cancellationToken);

        Reload();
        return id is null ? null : _all.FirstOrDefault(s => s.Id == id);
    }

    private static long? Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? processName,
        string text)
    {
        var now = Timestamps.ToText(DateTimeOffset.UtcNow);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // The conflict target mirrors ux_instructions_scope, which folds NULL to '*' so there can
        // only ever be one global set.
        command.CommandText = """
            INSERT INTO instructions (process_name, text, enabled, created_at, updated_at)
            VALUES ($process, $text, 1, $now, $now)
            ON CONFLICT (IFNULL(process_name, '*') COLLATE NOCASE) DO UPDATE SET
                text = excluded.text,
                updated_at = excluded.updated_at
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$process", (object?)processName ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now);

        return (long)command.ExecuteScalar()!;
    }

    private static long? Delete(SqliteConnection connection, SqliteTransaction transaction, string? processName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = processName is null
            ? "DELETE FROM instructions WHERE process_name IS NULL;"
            : "DELETE FROM instructions WHERE process_name = $process COLLATE NOCASE;";

        if (processName is not null)
        {
            command.Parameters.AddWithValue("$process", processName);
        }

        command.ExecuteNonQuery();
        return null;
    }

    /// <summary>
    /// Folds a typed-in process name into the form the focus tracker reports.
    /// </summary>
    private static string? Normalise(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var trimmed = processName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToLowerInvariant();
    }
}
