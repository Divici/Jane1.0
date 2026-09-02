using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jane.Core.Platform;
using Jane.Core.Settings;
using Jane.Core.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace Jane.Core.Storage;

/// <summary>
/// Jane's one database: <c>%LOCALAPPDATA%\Jane\jane.db</c>, its schema, and the runner that
/// brings it up to date.
/// </summary>
/// <remarks>
/// <para>
/// Everything Jane keeps about a person lives in this one file, in plaintext, until they delete
/// it. That is a stated design constraint rather than an oversight: a local dictation tool that
/// encrypted its own history would be keeping a secret from its only user, and the key would have
/// to live next to the file anyway. What the design owes in exchange is honesty -- a schema that
/// reads plainly under <c>sqlite3 jane.db ".schema"</c>, no audio anywhere, and a delete that
/// actually reclaims the space.
/// </para>
/// <para>
/// One owned connection, serialised by a semaphore. Jane writes once per dictation and reads a
/// handful of cached rows, so nothing here is contended, and a single connection means no
/// write-ahead log and therefore no <c>-wal</c>/<c>-shm</c> sidecars -- the file a person copies
/// or deletes is the whole database. Other processes (a <c>sqlite3</c> session, a second Jane)
/// are handled by SQLite's own file locking plus <c>busy_timeout</c>.
/// </para>
/// </remarks>
public sealed class JaneDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private JaneDatabase(JanePaths paths, SqliteConnection connection, IReadOnlyList<AppliedMigration> applied)
    {
        Paths = paths;
        _connection = connection;
        AppliedMigrations = applied;
    }

    /// <summary>Where the rest of Jane's files live. The stores read <c>SettingsFile</c> from here.</summary>
    public JanePaths Paths { get; }

    public string Path => Paths.Database;

    /// <summary>Highest migration applied. Zero only for a database that failed to migrate.</summary>
    public int SchemaVersion => AppliedMigrations.Count == 0 ? 0 : AppliedMigrations.Max(m => m.Version);

    /// <summary>The <c>schema_migrations</c> ledger, oldest first. Shown in the About view.</summary>
    public IReadOnlyList<AppliedMigration> AppliedMigrations { get; }

    /// <summary>
    /// Opens the database, creating and migrating it if necessary.
    /// </summary>
    /// <exception cref="MigrationFailedException">
    /// A migration threw. The database is left at the version before it, not half-applied.
    /// </exception>
    public static JaneDatabase Open(JanePaths paths) => Open(paths, JaneMigrations.All);

    internal static JaneDatabase Open(JanePaths paths, IReadOnlyList<Migration> migrations)
    {
        Directory.CreateDirectory(paths.Root);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Pooling would keep a handle on the file after Dispose, which breaks both the
            // "delete everything" path and any test that removes its temp directory.
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        try
        {
            Configure(connection);
            var applied = Migrate(connection, migrations);
            return new JaneDatabase(paths, connection, applied);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // busy_timeout so a concurrent `sqlite3` session or a second Jane waits rather than
        // failing outright; foreign_keys because a later migration will want them and turning
        // them on afterwards would not retro-check existing rows.
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static List<AppliedMigration> Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        // The ledger is the runner's own bookkeeping, so it is created here rather than by a
        // migration -- a migration cannot record itself in a table that does not exist yet.
        using (var ledger = connection.CreateCommand())
        {
            ledger.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            ledger.ExecuteNonQuery();
        }

        var applied = ReadLedger(connection);
        var known = applied.Select(m => m.Version).ToHashSet();

        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (known.Contains(migration.Version))
            {
                continue;
            }

            // One transaction per migration, covering both the schema change and the ledger row.
            // A throw anywhere inside rolls back both, so the runner never sees a version it
            // believes is applied over a schema that is only half there.
            using var transaction = connection.BeginTransaction();
            var appliedAt = DateTimeOffset.UtcNow;

            try
            {
                migration.Apply(connection, transaction);

                using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $applied);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$applied", Timestamps.ToText(appliedAt));
                record.ExecuteNonQuery();

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new MigrationFailedException(migration.Version, migration.Name, ex);
            }

            applied.Add(new AppliedMigration(migration.Version, migration.Name, appliedAt));
        }

        return applied;
    }

    private static List<AppliedMigration> ReadLedger(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, applied_at FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();

        var applied = new List<AppliedMigration>();
        while (reader.Read())
        {
            applied.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                Timestamps.FromText(reader.GetString(2))));
        }

        return applied;
    }

    /// <summary>
    /// Rebuilds the file so deleted rows stop occupying disk.
    /// </summary>
    /// <remarks>
    /// SQLite reuses freed pages but never shrinks the file on its own, so without this a
    /// "delete everything" is a UI label over a file that is still exactly as large as the words
    /// it used to hold. <c>VACUUM</c> cannot run inside a transaction, which is why it is its own
    /// operation rather than part of a delete.
    /// </remarks>
    public Task VacuumAsync(CancellationToken cancellationToken) =>
        ExecuteAsync("VACUUM;", _ => { }, cancellationToken);

    /// <summary>Bytes the database currently occupies on disk.</summary>
    public long FileSizeBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    /// <summary>Runs a read against the owned connection. Internal: connections stay in here.</summary>
    internal async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return read(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a write inside one transaction. Everything the caller does either lands or does not.
    /// </summary>
    internal async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, T> write,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return WriteCore(write);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous write, for a store seeding itself during construction.</summary>
    internal T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gate.Wait();
        try
        {
            return WriteCore(write);
        }
        finally
        {
            _gate.Release();
        }
    }

    private T WriteCore<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        using var transaction = _connection.BeginTransaction();
        var result = write(_connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>Runs a statement that cannot live in a transaction, such as <c>VACUUM</c>.</summary>
    internal async Task ExecuteAsync(string sql, Action<SqliteCommand> configure, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            configure(command);
            command.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous read, for the hot path where a store is filling its in-memory cache.</summary>
    internal T Read<T>(Func<SqliteConnection, T> read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gate.Wait();
        try
        {
            return read(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Reads and writes <see cref="JaneSettings"/> in SQLite, and carries the Phase 1
/// <c>settings.json</c> across on first open.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JaneSettings"/> stays the in-memory model after the migration -- only the storage
/// changes. Each leaf of that object becomes one row keyed by its dotted JSON path
/// (<c>speech.numThreads</c>, <c>llm.keepAlive</c>), holding the JSON representation of the value.
/// That keeps the round trip exact while leaving the table legible to a person with
/// <c>sqlite3</c>, which a single opaque JSON blob would not.
/// </para>
/// <para>
/// The import from <c>settings.json</c> is one-way and happens only when the settings table is
/// empty. `bench` still writes that file, so re-importing on every open would let a stale file
/// silently undo a change made in the settings window.
/// </para>
/// </remarks>
public sealed class SettingsRepository
{
    /// <summary>
    /// The same options <c>SettingsStore</c> uses, so property names and enum shapes match the
    /// Phase 1 file exactly and the migration is lossless.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JaneDatabase _database;
    private JaneSettings _cached;

    public SettingsRepository(JaneDatabase database)
    {
        _database = database;

        var rows = ReadRows();
        if (rows.Count > 0)
        {
            _cached = Materialise(rows);
            return;
        }

        var (seed, imported) = LoadSeed(database.Paths.SettingsFile);
        ImportedFromSettingsJson = imported;
        _cached = seed;
        Persist(seed);
    }

    /// <summary>
    /// True when this construction carried a Phase 1 <c>settings.json</c> into the database.
    /// </summary>
    /// <remarks>Onboarding shows this: "your existing settings were imported" is worth saying once.</remarks>
    public bool ImportedFromSettingsJson { get; }

    /// <summary>Last value read or written. Cheap; the pipeline reads this on every dictation.</summary>
    public JaneSettings Current => _cached;

    /// <summary>Raised after a successful write, so an open settings window and the overlay agree.</summary>
    public event EventHandler<JaneSettings>? Changed;

    /// <summary>Re-reads every row. Falls back to defaults for an unreadable table.</summary>
    public JaneSettings Read()
    {
        _cached = Materialise(ReadRows());
        return _cached;
    }

    public async Task WriteAsync(JaneSettings settings, CancellationToken cancellationToken)
    {
        var leaves = Flatten(settings);

        await _database.WriteAsync((connection, transaction) =>
        {
            Replace(connection, transaction, leaves);
            return true;
        }, cancellationToken);

        _cached = settings;
        Changed?.Invoke(this, settings);
    }

    /// <summary>Applies a change to the current value and persists it.</summary>
    public Task UpdateAsync(Func<JaneSettings, JaneSettings> update, CancellationToken cancellationToken) =>
        WriteAsync(update(Read()), cancellationToken);

    private void Persist(JaneSettings settings)
    {
        var leaves = Flatten(settings);
        _database.Write((connection, transaction) =>
        {
            Replace(connection, transaction, leaves);
            return true;
        });
    }

    /// <summary>
    /// Rewrites the whole table.
    /// </summary>
    /// <remarks>
    /// The settings object is always written whole, so a delete-then-insert inside the one
    /// transaction is both simpler and more correct than diffing: it removes keys that are no
    /// longer set, which an upsert would silently leave behind.
    /// </remarks>
    private static void Replace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, string> leaves)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM settings;";
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value);";
        var key = insert.Parameters.Add("$key", SqliteType.Text);
        var value = insert.Parameters.Add("$value", SqliteType.Text);

        foreach (var (name, json) in leaves)
        {
            key.Value = name;
            value.Value = json;
            insert.ExecuteNonQuery();
        }
    }

    private Dictionary<string, string> ReadRows() => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";
        using var reader = command.ExecuteReader();

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    });

    /// <summary>
    /// What a database with no settings rows should start from: the Phase 1 file if there is one,
    /// otherwise the defaults.
    /// </summary>
    private static (JaneSettings Settings, bool Imported) LoadSeed(string settingsJsonPath)
    {
        if (!File.Exists(settingsJsonPath))
        {
            return (new JaneSettings(), false);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<JaneSettings>(File.ReadAllText(settingsJsonPath), Json);
            return settings is null ? (new JaneSettings(), false) : (settings, true);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt Phase 1 file must not stop Jane starting, exactly as it does not stop
            // SettingsStore. Defaults are all valid and the next write repairs the database.
            return (new JaneSettings(), false);
        }
    }

    private static Dictionary<string, string> Flatten(JaneSettings settings)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(string.Empty, JsonSerializer.SerializeToNode(settings, Json), leaves);
        return leaves;
    }

    private static void Walk(string prefix, JsonNode? node, Dictionary<string, string> leaves)
    {
        if (node is JsonObject nested)
        {
            foreach (var (name, child) in nested)
            {
                Walk(prefix.Length == 0 ? name : $"{prefix}.{name}", child, leaves);
            }

            return;
        }

        // Scalars and arrays alike are stored as their JSON text. JaneSettings has no arrays
        // today; if one appears it lands whole in a single row rather than as key.0, key.1, which
        // would make removing an element a multi-row delete.
        leaves[prefix] = node?.ToJsonString(Json) ?? "null";
    }

    private static JaneSettings Materialise(IReadOnlyDictionary<string, string> rows)
    {
        if (rows.Count == 0)
        {
            return new JaneSettings();
        }

        try
        {
            var root = new JsonObject();

            foreach (var (key, value) in rows)
            {
                var segments = key.Split('.');
                var cursor = root;

                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (cursor[segments[i]] is not JsonObject next)
                    {
                        next = [];
                        cursor[segments[i]] = next;
                    }

                    cursor = next;
                }

                cursor[segments[^1]] = JsonNode.Parse(value);
            }

            return root.Deserialize<JaneSettings>(Json) ?? new JaneSettings();
        }
        catch (JsonException)
        {
            // A row edited by hand into something invalid costs the user their settings, not
            // their ability to start Jane.
            return new JaneSettings();
        }
    }
}

/// <summary>
/// How Jane writes an instant into the database.
/// </summary>
/// <remarks>
/// ISO-8601 in UTC, round-trip precision. UTC because lexicographic ordering is then chronological
/// ordering, so history sorts with a plain <c>ORDER BY created_at</c> and no date functions; the
/// UI converts to local time for display.
/// </remarks>
internal static class Timestamps
{
    public static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset FromText(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
