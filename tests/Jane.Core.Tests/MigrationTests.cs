using Jane.Core.Abstractions;
using Jane.Core.Platform;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Core.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace Jane.Core.Tests;

/// <summary>
/// The migration runner and the Phase 1 JSON handover.
/// </summary>
/// <remarks>
/// Every test gets its own temp root, so nothing here can see or touch the real
/// <c>%LOCALAPPDATA%\Jane</c>.
/// </remarks>
public sealed class MigrationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-migration-test");

    private JanePaths Paths => new(_root.FullName, Path.Combine(_root.FullName, "models"));

    [Fact]
    public void EmptyDatabase_MigratesToTheCurrentSchemaVersion()
    {
        using var database = JaneDatabase.Open(Paths);

        Assert.Equal(JaneMigrations.CurrentVersion, database.SchemaVersion);
        Assert.True(File.Exists(database.Path));
        Assert.NotEmpty(database.AppliedMigrations);
    }

    [Fact]
    public void EveryTableTheStoresNeed_ExistsAfterMigration()
    {
        using (JaneDatabase.Open(Paths))
        {
        }

        var tables = ReadTables();

        foreach (var expected in (string[])["schema_migrations", "settings", "dictionary", "instructions", "history"])
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void OpeningTwiceIsIdempotent_TheSecondRunAppliesNothingNew()
    {
        using (var first = JaneDatabase.Open(Paths))
        {
            Assert.NotEmpty(first.AppliedMigrations);
        }

        using var second = JaneDatabase.Open(Paths);

        Assert.Equal(JaneMigrations.CurrentVersion, second.SchemaVersion);
        Assert.Equal(JaneMigrations.All.Count, second.AppliedMigrations.Count);
    }

    [Fact]
    public void MigrationsAreForwardOnly_AndRunInVersionOrder()
    {
        var order = new List<int>();
        List<Migration> migrations =
        [
            new(3, "third", (_, _) => order.Add(3)),
            new(1, "first", (_, _) => order.Add(1)),
            new(2, "second", (_, _) => order.Add(2)),
        ];

        using var database = JaneDatabase.Open(Paths, migrations);

        Assert.Equal([1, 2, 3], order);
        Assert.Equal(3, database.SchemaVersion);
    }

    [Fact]
    public void AlreadyAppliedMigrationsAreNeverReRun()
    {
        var runs = 0;
        List<Migration> migrations = [new(1, "counted", (_, _) => runs++)];

        using (JaneDatabase.Open(Paths, migrations))
        {
        }

        using (JaneDatabase.Open(Paths, migrations))
        {
        }

        Assert.Equal(1, runs);
    }

    [Fact]
    public void FailedMigration_RollsBackItsWholeTransactionAndLeavesThePriorVersion()
    {
        // A half-applied migration is the worst outcome available: the schema no longer matches
        // either version, and the runner would skip it on the next launch.
        List<Migration> good = [JaneMigrations.All[0]];
        List<Migration> broken =
        [
            JaneMigrations.All[0],
            new(9_999, "half-applied", (connection, transaction) =>
            {
                Execute(connection, transaction, "CREATE TABLE doomed (id INTEGER PRIMARY KEY);");
                throw new InvalidOperationException("deliberate failure halfway through");
            }),
        ];

        using (JaneDatabase.Open(Paths, good))
        {
        }

        Assert.Throws<MigrationFailedException>(() => JaneDatabase.Open(Paths, broken));

        Assert.DoesNotContain("doomed", ReadTables());

        using var reopened = JaneDatabase.Open(Paths, good);
        Assert.Equal(good[0].Version, reopened.SchemaVersion);
    }

    [Fact]
    public async Task SettingsRoundTripThroughSqlite_PreservingEveryNestedSection()
    {
        var original = new JaneSettings
        {
            Speech = new SpeechSettings("whisper-large-v3-turbo-q8_0", 2, true, 2.5f, "whisper-large-v3-turbo-q8_0"),
            Hotkey = new HotkeySettings(0x14, HotkeyMode.Toggle, 250, 60_000, EnabledInGame: true),
            Llm = new LlmSettings("m-gpu", "m-cpu", "90s", 90, 4096, 2, InGameBehaviour.UseCpuLlm, Enabled: false),
            Overlay = new OverlaySettings(Visible: false, ShowContextIndicator: false),
            Gpu = new GpuSettings(1024, 55, false, false, false),
            MicrophoneDeviceId = "{0.0.1.00000000}",
            OnboardingComplete = true,
            BenchmarkedAt = DateTimeOffset.Now,
        };

        using (var database = JaneDatabase.Open(Paths))
        {
            var settings = new SettingsRepository(database);
            await settings.WriteAsync(original, TestContext.Current.CancellationToken);
        }

        using var reopened = JaneDatabase.Open(Paths);
        var readBack = new SettingsRepository(reopened).Read();

        Assert.Equal(original, readBack);
    }

    [Fact]
    public void MigrationFromAnEmptyDatabase_SeedsTheDefaults()
    {
        // No settings.json anywhere: the table still ends up populated, so `sqlite3 jane.db
        // "select * from settings"` shows the real configuration rather than nothing at all.
        Assert.False(File.Exists(Paths.SettingsFile));

        using var database = JaneDatabase.Open(Paths);
        var settings = new SettingsRepository(database);

        Assert.False(settings.ImportedFromSettingsJson);
        Assert.Equal(new JaneSettings(), settings.Read());
        Assert.NotEmpty(ReadSettingsKeys());
    }

    [Fact]
    public async Task MigrationFromSettingsJson_CarriesThePhase1FileIntoTheDatabase()
    {
        using (var phase1 = new SettingsStore(Paths))
        {
            await phase1.WriteAsync(
                new JaneSettings
                {
                    Speech = new SpeechSettings("parakeet-tdt-0.6b-v2-int8", 4, EnableHotwordBiasing: true),
                    Hotkey = new HotkeySettings(0x14, HotkeyMode.Toggle, 250, 90_000),
                    OnboardingComplete = true,
                },
                TestContext.Current.CancellationToken);
        }

        using var database = JaneDatabase.Open(Paths);
        var settings = new SettingsRepository(database);

        Assert.True(settings.ImportedFromSettingsJson);

        var migrated = settings.Read();
        Assert.True(migrated.Speech.EnableHotwordBiasing);
        Assert.Equal(HotkeyMode.Toggle, migrated.Hotkey.Mode);
        Assert.Equal(90_000, migrated.Hotkey.MaxToggleDurationMs);
        Assert.True(migrated.OnboardingComplete);
    }

    [Fact]
    public async Task SettingsJsonImportIsIdempotent_AndNeverClobbersALaterChange()
    {
        // The Phase 1 file is not deleted on import -- `bench` still writes it. So the import
        // has to be a one-way handover, or a stale file would silently undo the user's edits.
        using (var phase1 = new SettingsStore(Paths))
        {
            await phase1.WriteAsync(
                new JaneSettings { Speech = new SpeechSettings(NumThreads: 2) },
                TestContext.Current.CancellationToken);
        }

        using (var database = JaneDatabase.Open(Paths))
        {
            var settings = new SettingsRepository(database);
            Assert.True(settings.ImportedFromSettingsJson);
            await settings.UpdateAsync(
                s => s with { Speech = s.Speech with { NumThreads = 8 } },
                TestContext.Current.CancellationToken);
        }

        using var reopened = JaneDatabase.Open(Paths);
        var second = new SettingsRepository(reopened);

        Assert.False(second.ImportedFromSettingsJson);
        Assert.Equal(8, second.Read().Speech.NumThreads);
    }

    [Fact]
    public async Task SettingsTableIsReadableWithSqlite3_DottedKeysAndJsonLeaves()
    {
        // Privacy here is a design constraint, not a feature: a person must be able to open the
        // file and understand what Jane kept about them without running Jane.
        using (var database = JaneDatabase.Open(Paths))
        {
            var settings = new SettingsRepository(database);
            await settings.UpdateAsync(
                s => s with { Speech = s.Speech with { NumThreads = 6 } },
                TestContext.Current.CancellationToken);
        }

        var rows = ReadSettings();

        Assert.Equal("6", rows["speech.numThreads"]);
        Assert.Equal("\"180s\"", rows["llm.keepAlive"]);
        Assert.Equal("true", rows["overlay.visible"]);
        Assert.DoesNotContain(rows.Keys, k => k.Contains(' ', StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsWriteRemovesKeysThatAreNoLongerSet()
    {
        using var database = JaneDatabase.Open(Paths);
        var settings = new SettingsRepository(database);

        await settings.UpdateAsync(s => s with { MicrophoneDeviceId = "{0.0.1.00000000}" },
            TestContext.Current.CancellationToken);
        Assert.Contains("microphoneDeviceId", ReadSettingsKeys());

        await settings.UpdateAsync(s => s with { MicrophoneDeviceId = null },
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("microphoneDeviceId", ReadSettingsKeys());
    }

    [Fact]
    public async Task SettingsChangedFires_SoTheOverlayToggleTakesEffectWithoutARestart()
    {
        using var database = JaneDatabase.Open(Paths);
        var settings = new SettingsRepository(database);

        JaneSettings? observed = null;
        settings.Changed += (_, s) => observed = s;

        await settings.UpdateAsync(s => s with { Overlay = s.Overlay with { Visible = false } },
            TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.False(observed.Overlay.Visible);
    }

    [Fact]
    public void DatabaseLivesWhereJanePathsSaysItDoes()
    {
        using var database = JaneDatabase.Open(Paths);

        Assert.Equal(Paths.Database, database.Path);
        Assert.EndsWith("jane.db", database.Path, StringComparison.Ordinal);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<string> ReadTables()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private IReadOnlyDictionary<string, string> ReadSettings()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";
        using var reader = command.ExecuteReader();

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    private IReadOnlyList<string> ReadSettingsKeys() => [.. ReadSettings().Keys];

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={Paths.Database};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Dispose() => _root.Delete(recursive: true);
}
