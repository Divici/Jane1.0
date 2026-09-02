using System.Text;
using Jane.Core.Abstractions;
using Jane.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Jane.Core.History;

/// <summary>
/// Every dictation Jane has ever done, in plaintext SQLite, until the user deletes it.
/// </summary>
/// <remarks>
/// <para>
/// This is the store the history window is built on: search, copy, re-inject, delete one, delete
/// everything. It is also the record Phase 12 measures the false-bypass rate from, which is why
/// the bypass decision and its reason are written on every row rather than only the interesting
/// ones.
/// </para>
/// <para>
/// <b>Audio is never written here.</b> There is no column, parameter or overload that could carry
/// it. What is written is what was said and what was typed, and the window says so plainly --
/// the promise the plan makes is only honest if a delete really reclaims the space, which is why
/// <see cref="DeleteAllAsync"/> vacuums rather than merely emptying the table.
/// </para>
/// </remarks>
public sealed class HistoryStore
{
    private const string Columns = """
        id, created_at, mode, raw_transcript, final_text, deep_context,
        target_handle, target_process_id, target_process_name, target_window_class, target_window_title,
        bypassed, bypass_reason, engine_id, llm_model, injected, injection_failure,
        capture_ms, vad_ms, recognition_ms, context_ms, formatting_ms, injection_ms, total_ms
        """;

    /// <summary>
    /// Newest first, with the row id as the tie-break.
    /// </summary>
    /// <remarks>
    /// <c>DateTimeOffset.Now</c> ticks at roughly 15 ms on Windows, so two dictations in quick
    /// succession can share a timestamp exactly. Without the id the order between them would be
    /// whatever the query planner felt like.
    /// </remarks>
    private const string NewestFirst = " ORDER BY created_at DESC, id DESC";

    private readonly JaneDatabase _database;

    public HistoryStore(JaneDatabase database) => _database = database;

    /// <summary>Raised whenever a row was actually added or removed, so an open window refreshes.</summary>
    public event EventHandler? Changed;

    /// <summary>Writes one dictation and returns it with its assigned id.</summary>
    public async Task<HistoryEntry> AppendAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var id = await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO history (
                    created_at, mode, raw_transcript, final_text, deep_context,
                    target_handle, target_process_id, target_process_name, target_window_class, target_window_title,
                    bypassed, bypass_reason, engine_id, llm_model, injected, injection_failure,
                    capture_ms, vad_ms, recognition_ms, context_ms, formatting_ms, injection_ms, total_ms)
                VALUES (
                    $created, $mode, $raw, $final, $context,
                    $handle, $pid, $process, $class, $title,
                    $bypassed, $reason, $engine, $model, $injected, $failure,
                    $capture, $vad, $recognition, $contextMs, $formatting, $injection, $total)
                RETURNING id;
                """;

            command.Parameters.AddWithValue("$created", Timestamps.ToText(entry.CreatedAt));
            command.Parameters.AddWithValue("$mode", entry.Mode.ToString());
            command.Parameters.AddWithValue("$raw", entry.RawTranscript);
            command.Parameters.AddWithValue("$final", entry.FinalText);
            command.Parameters.AddWithValue("$context", (object?)entry.DeepContext ?? DBNull.Value);

            command.Parameters.AddWithValue("$handle", (long)entry.Target.Handle);
            command.Parameters.AddWithValue("$pid", entry.Target.ProcessId);
            command.Parameters.AddWithValue("$process", entry.Target.ProcessName);
            command.Parameters.AddWithValue("$class", entry.Target.WindowClass);
            command.Parameters.AddWithValue("$title", entry.Target.WindowTitle);

            command.Parameters.AddWithValue("$bypassed", entry.Bypassed ? 1 : 0);
            command.Parameters.AddWithValue("$reason", entry.BypassReason);
            command.Parameters.AddWithValue("$engine", entry.EngineId);
            command.Parameters.AddWithValue("$model", (object?)entry.LlmModel ?? DBNull.Value);
            command.Parameters.AddWithValue("$injected", entry.Injected ? 1 : 0);
            command.Parameters.AddWithValue("$failure", (object?)entry.InjectionFailure ?? DBNull.Value);

            command.Parameters.AddWithValue("$capture", Millis(entry.Timings.Capture));
            command.Parameters.AddWithValue("$vad", Millis(entry.Timings.Vad));
            command.Parameters.AddWithValue("$recognition", Millis(entry.Timings.Recognition));
            command.Parameters.AddWithValue("$contextMs", Millis(entry.Timings.Context));
            command.Parameters.AddWithValue("$formatting", Millis(entry.Timings.Formatting));
            command.Parameters.AddWithValue("$injection", Millis(entry.Timings.Injection));
            command.Parameters.AddWithValue("$total", Millis(entry.Timings.Total));

            return (long)command.ExecuteScalar()!;
        }, cancellationToken);

        Changed?.Invoke(this, EventArgs.Empty);

        // Returned with the timings rounded exactly as they were stored, so what the caller holds
        // and what a later read produces are the same object.
        return entry with { Id = id, Timings = Round(entry.Timings) };
    }

    public Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken) =>
        _database.ReadAsync<HistoryEntry?>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM history WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }, cancellationToken);

    /// <summary>The most recent entries, newest first. What the history window opens on.</summary>
    public Task<IReadOnlyList<HistoryEntry>> RecentAsync(int limit, CancellationToken cancellationToken) =>
        SearchAsync(new HistoryQuery { Limit = limit }, cancellationToken);

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        _database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = BuildSearch(query, command);

            using var reader = command.ExecuteReader();
            var rows = new List<HistoryEntry>();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }

            return (IReadOnlyList<HistoryEntry>)rows;
        }, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        _database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM history;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }, cancellationToken);

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var deleted = await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }, cancellationToken);

        if (deleted)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes every entry and compacts the file. Returns how many rows went.
    /// </summary>
    /// <remarks>
    /// The <c>VACUUM</c> is the point. SQLite reuses freed pages but never shrinks the file on its
    /// own, so without it "delete everything" would leave a database exactly as large as the
    /// words it used to hold, and forensically just as readable. It cannot run inside a
    /// transaction, so it follows the delete rather than joining it -- a crash in between leaves
    /// an empty database that is merely larger than it needs to be.
    /// </remarks>
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        var deleted = await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history;";
            return command.ExecuteNonQuery();
        }, cancellationToken);

        if (deleted == 0)
        {
            return 0;
        }

        await _database.VacuumAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    /// <summary>
    /// Builds the WHERE clause from whichever filters are set.
    /// </summary>
    /// <remarks>
    /// Every value is a parameter and every fragment is a compile-time constant, so no part of a
    /// transcript or a process name is ever concatenated into SQL.
    /// </remarks>
    private static string BuildSearch(HistoryQuery query, SqliteCommand command)
    {
        var sql = new StringBuilder("SELECT ").Append(Columns).Append(" FROM history");
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            clauses.Add("(raw_transcript LIKE $text ESCAPE '\\' OR final_text LIKE $text ESCAPE '\\')");
            command.Parameters.AddWithValue("$text", $"%{EscapeLike(query.Text)}%");
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessName))
        {
            clauses.Add("target_process_name = $process COLLATE NOCASE");
            command.Parameters.AddWithValue("$process", query.ProcessName.Trim());
        }

        if (query.Mode is { } mode)
        {
            clauses.Add("mode = $mode");
            command.Parameters.AddWithValue("$mode", mode.ToString());
        }

        if (query.Bypassed is { } bypassed)
        {
            clauses.Add("bypassed = $bypassed");
            command.Parameters.AddWithValue("$bypassed", bypassed ? 1 : 0);
        }

        if (query.Since is { } since)
        {
            clauses.Add("created_at >= $since");
            command.Parameters.AddWithValue("$since", Timestamps.ToText(since));
        }

        if (query.Until is { } until)
        {
            clauses.Add("created_at <= $until");
            command.Parameters.AddWithValue("$until", Timestamps.ToText(until));
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", clauses);
        }

        sql.Append(NewestFirst).Append(" LIMIT $limit OFFSET $offset;");
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        return sql.ToString();
    }

    /// <summary>
    /// Makes a search term literal.
    /// </summary>
    /// <remarks>
    /// Without this, searching for "100%" would match every row, and "_" would match any single
    /// character -- a search box that silently means something else is worse than no search box.
    /// </remarks>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static HistoryEntry Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        CreatedAt = Timestamps.FromText(reader.GetString(1)),
        Mode = Enum.TryParse<DictationMode>(reader.GetString(2), out var mode) ? mode : DictationMode.Dictation,
        RawTranscript = reader.GetString(3),
        FinalText = reader.GetString(4),
        DeepContext = reader.IsDBNull(5) ? null : reader.GetString(5),
        Target = new TargetWindow(
            (nint)reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10)),
        Bypassed = reader.GetInt64(11) != 0,
        BypassReason = reader.GetString(12),
        EngineId = reader.GetString(13),
        LlmModel = reader.IsDBNull(14) ? null : reader.GetString(14),
        Injected = reader.GetInt64(15) != 0,
        InjectionFailure = reader.IsDBNull(16) ? null : reader.GetString(16),
        Timings = new StageTimings(
            TimeSpan.FromMilliseconds(reader.GetInt64(17)),
            TimeSpan.FromMilliseconds(reader.GetInt64(18)),
            TimeSpan.FromMilliseconds(reader.GetInt64(19)),
            TimeSpan.FromMilliseconds(reader.GetInt64(20)),
            TimeSpan.FromMilliseconds(reader.GetInt64(21)),
            TimeSpan.FromMilliseconds(reader.GetInt64(22)),
            TimeSpan.FromMilliseconds(reader.GetInt64(23))),
    };

    private static long Millis(TimeSpan span) => (long)Math.Round(span.TotalMilliseconds);

    private static StageTimings Round(StageTimings timings) => new(
        TimeSpan.FromMilliseconds(Millis(timings.Capture)),
        TimeSpan.FromMilliseconds(Millis(timings.Vad)),
        TimeSpan.FromMilliseconds(Millis(timings.Recognition)),
        TimeSpan.FromMilliseconds(Millis(timings.Context)),
        TimeSpan.FromMilliseconds(Millis(timings.Formatting)),
        TimeSpan.FromMilliseconds(Millis(timings.Injection)),
        TimeSpan.FromMilliseconds(Millis(timings.Total)));
}
