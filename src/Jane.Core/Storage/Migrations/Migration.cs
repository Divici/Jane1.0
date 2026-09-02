using Microsoft.Data.Sqlite;

namespace Jane.Core.Storage.Migrations;

/// <summary>
/// One forward-only step in the shape of <c>jane.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// Internal on purpose. Nothing outside <c>Jane.Core</c> writes a migration, and keeping the type
/// internal is what stops <c>Microsoft.Data.Sqlite</c> leaking into Jane's public surface -- the
/// UI talks to <see cref="JaneDatabase"/> and the stores, never to a connection.
/// </para>
/// <para>
/// <see cref="Apply"/> receives the open connection and the transaction the runner already
/// started. A migration must not commit, roll back, or open a transaction of its own: the runner
/// owns the boundary so that a throw halfway through leaves the previous version intact.
/// </para>
/// </remarks>
/// <param name="Version">Strictly increasing. Gaps are legal; duplicates are not.</param>
/// <param name="Name">Written into <c>schema_migrations</c> so the ledger reads as prose.</param>
internal sealed record Migration(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Apply)
{
    /// <summary>A migration that is nothing but SQL. Statements are separated by semicolons.</summary>
    public static Migration Sql(int version, string name, string sql) =>
        new(version, name, (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });
}

/// <summary>What the migration runner recorded in <c>schema_migrations</c>.</summary>
/// <remarks>
/// Public because the About and diagnostics surfaces show it: "schema 1, applied 2 March 2026"
/// is the one line that makes a support question answerable without opening the file.
/// </remarks>
public sealed record AppliedMigration(int Version, string Name, DateTimeOffset AppliedAt);

/// <summary>
/// The database could not be brought to the current schema.
/// </summary>
/// <remarks>
/// Fatal to the data layer and nothing else: the tray icon, the overlay and raw dictation all
/// still work without history. The caller shows a designed error rather than failing to start.
/// </remarks>
public sealed class MigrationFailedException(int version, string name, Exception inner)
    : Exception($"Migration {version} ({name}) failed and was rolled back.", inner)
{
    public int Version { get; } = version;

    public string MigrationName { get; } = name;
}
