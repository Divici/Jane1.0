using System.Reflection;
using Jane.Core.Abstractions;
using Jane.Core.History;
using Jane.Core.Platform;
using Jane.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Jane.Core.Tests;

/// <summary>
/// History: plaintext, permanent until deleted, and never audio.
/// </summary>
/// <remarks>
/// The plan is explicit that everything ever dictated is kept in plaintext SQLite and that the UI
/// says so plainly. These tests are what make that promise honest in both directions -- no audio
/// is ever written, and a delete really does reclaim the space.
/// </remarks>
public sealed class HistoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-history-test");
    private JaneDatabase _database;
    private HistoryStore _history;

    public HistoryTests()
    {
        _database = JaneDatabase.Open(Paths);
        _history = new HistoryStore(_database);
    }

    private JanePaths Paths => new(_root.FullName, Path.Combine(_root.FullName, "models"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TargetWindow Window(string process, nint handle = 0x1234, int pid = 4242) =>
        new(handle, pid, process, "Notepad", $"Untitled - {process}");

    private static HistoryEntry Draft(string raw, string final, TargetWindow? target = null) => new()
    {
        RawTranscript = raw,
        FinalText = final,
        Target = target ?? Window("notepad"),
        EngineId = "parakeet-tdt-0.6b-v2-int8",
        Injected = true,
    };

    [Fact]
    public async Task HistoryPersistsAcrossRestart()
    {
        await _history.AppendAsync(Draft("um so send it tuesday", "Send it Tuesday."), Token);
        _database.Dispose();

        _database = JaneDatabase.Open(Paths);
        _history = new HistoryStore(_database);

        var rows = await _history.RecentAsync(10, Token);

        Assert.Single(rows);
        Assert.Equal("um so send it tuesday", rows[0].RawTranscript);
        Assert.Equal("Send it Tuesday.", rows[0].FinalText);
    }

    [Fact]
    public async Task AppendReturnsTheRowWithItsAssignedId()
    {
        var stored = await _history.AppendAsync(Draft("hello", "Hello."), Token);

        Assert.NotEqual(0, stored.Id);
        Assert.Equal(stored, await _history.GetAsync(stored.Id, Token));
    }

    [Fact]
    public async Task SearchReturnsTheRightRows()
    {
        await _history.AppendAsync(Draft("deploy to kubernetes", "Deploy to Kubernetes.", Window("code")), Token);
        await _history.AppendAsync(Draft("lunch at one", "Lunch at one.", Window("slack")), Token);
        await _history.AppendAsync(Draft("scale the kubernetes cluster", "Scale the Kubernetes cluster.", Window("code")), Token);

        var byText = await _history.SearchAsync(new HistoryQuery { Text = "kubernetes" }, Token);
        Assert.Equal(2, byText.Count);
        Assert.All(byText, e => Assert.Contains("ubernetes", e.FinalText, StringComparison.Ordinal));

        var byProcess = await _history.SearchAsync(new HistoryQuery { ProcessName = "slack" }, Token);
        Assert.Single(byProcess);
        Assert.Equal("Lunch at one.", byProcess[0].FinalText);

        var none = await _history.SearchAsync(new HistoryQuery { Text = "prometheus" }, Token);
        Assert.Empty(none);
    }

    [Fact]
    public async Task SearchMatchesTheRawTranscriptToo_SoAMisheardWordIsStillFindable()
    {
        await _history.AppendAsync(Draft("poll request", "Pull request."), Token);

        var found = await _history.SearchAsync(new HistoryQuery { Text = "poll" }, Token);

        Assert.Single(found);
    }

    [Fact]
    public async Task SearchTreatsWildcardCharactersAsLiteralText()
    {
        await _history.AppendAsync(Draft("literal 100% done", "Literal 100% done."), Token);
        await _history.AppendAsync(Draft("nothing like it", "Nothing like it."), Token);

        var found = await _history.SearchAsync(new HistoryQuery { Text = "100%" }, Token);

        Assert.Single(found);
    }

    [Fact]
    public async Task BypassDecisionAndReasonAreRecorded_SoPhase12CanMeasureFalseBypasses()
    {
        await _history.AppendAsync(
            Draft("send it tuesday", "Send it Tuesday.") with
            {
                Bypassed = true,
                BypassReason = "short, no filler, no instructions, no pending replacements",
            },
            Token);
        await _history.AppendAsync(
            Draft("um send it tuesday", "Send it Tuesday.") with
            {
                Bypassed = false,
                BypassReason = "filler lexicon hit: um",
                LlmModel = "jane-qwen3-4b",
            },
            Token);

        var bypassed = await _history.SearchAsync(new HistoryQuery { Bypassed = true }, Token);
        Assert.Single(bypassed);
        Assert.Contains("no pending replacements", bypassed[0].BypassReason, StringComparison.Ordinal);

        var formatted = await _history.SearchAsync(new HistoryQuery { Bypassed = false }, Token);
        Assert.Single(formatted);
        Assert.Equal("jane-qwen3-4b", formatted[0].LlmModel);
    }

    [Fact]
    public async Task StageTimingsRoundTripToTheMillisecond()
    {
        var timings = new StageTimings(
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(8),
            TimeSpan.FromMilliseconds(546),
            TimeSpan.FromMilliseconds(74),
            TimeSpan.FromMilliseconds(410),
            TimeSpan.FromMilliseconds(21),
            TimeSpan.FromMilliseconds(1071));

        var stored = await _history.AppendAsync(Draft("x", "X.") with { Timings = timings }, Token);
        var reloaded = await _history.GetAsync(stored.Id, Token);

        Assert.NotNull(reloaded);
        Assert.Equal(timings, reloaded.Timings);
        Assert.Equal(546, reloaded.Timings.Recognition.TotalMilliseconds);
    }

    [Fact]
    public async Task DeepContextCaptureIsStoredInPlaintextAlongsideTheTranscript()
    {
        var stored = await _history.AppendAsync(
            Draft("deploy it", "Deploy it.") with { DeepContext = "kubectl apply -f deployment.yaml" },
            Token);

        var reloaded = await _history.GetAsync(stored.Id, Token);

        Assert.NotNull(reloaded);
        Assert.Equal("kubectl apply -f deployment.yaml", reloaded.DeepContext);
    }

    [Fact]
    public async Task ModeIsRecordedAndFilterable()
    {
        await _history.AppendAsync(Draft("make it shorter", "Shorter.") with { Mode = DictationMode.Edit }, Token);
        await _history.AppendAsync(Draft("hello", "Hello."), Token);

        var edits = await _history.SearchAsync(new HistoryQuery { Mode = DictationMode.Edit }, Token);

        Assert.Single(edits);
        Assert.Equal(DictationMode.Edit, edits[0].Mode);
    }

    [Fact]
    public async Task HistoryRowsContainNoAudio()
    {
        await _history.AppendAsync(Draft("um so send it tuesday", "Send it Tuesday."), Token);
        _database.Dispose();

        using var connection = OpenRaw();

        // 1. No column anywhere is declared BLOB, and none is named for audio.
        foreach (var table in Tables(connection))
        {
            foreach (var (name, type) in Columns(connection, table))
            {
                Assert.DoesNotContain("BLOB", type, StringComparison.OrdinalIgnoreCase);

                foreach (var word in (string[])["audio", "pcm", "wav", "sample", "waveform", "recording"])
                {
                    Assert.DoesNotContain(word, name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // 2. No value actually stored in a history row is binary, whatever the declared type says
        //    -- SQLite's type affinity would happily accept a byte[] into a TEXT column.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM history;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        for (var i = 0; i < reader.FieldCount; i++)
        {
            Assert.NotEqual(typeof(byte[]), reader.GetFieldType(i));
            Assert.False(reader.GetValue(i) is byte[]);
        }
    }

    [Fact]
    public void HistoryEntryModelHasNoBinaryMembers()
    {
        // Belt to the schema's braces: nothing can be appended that would carry audio in the
        // first place, so a future caller cannot smuggle a buffer through a new column.
        foreach (var property in typeof(HistoryEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.NotEqual(typeof(byte[]), property.PropertyType);
            Assert.False(typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                && property.PropertyType != typeof(string));
        }
    }

    [Fact]
    public async Task DeleteRemovesOneRowAndLeavesTheRest()
    {
        var kept = await _history.AppendAsync(Draft("keep", "Keep."), Token);
        var dropped = await _history.AppendAsync(Draft("drop", "Drop."), Token);

        Assert.True(await _history.DeleteAsync(dropped.Id, Token));
        Assert.False(await _history.DeleteAsync(dropped.Id, Token));

        var rows = await _history.RecentAsync(10, Token);
        Assert.Single(rows);
        Assert.Equal(kept.Id, rows[0].Id);
    }

    [Fact]
    public async Task DeleteAllLeavesTheDatabaseEmptyAndCompacted()
    {
        // "Delete everything" has to reclaim the space, or the promise is a UI label over a file
        // that still holds every word.
        var filler = new string('x', 4_000);
        for (var i = 0; i < 300; i++)
        {
            await _history.AppendAsync(Draft($"raw {i} {filler}", $"final {i} {filler}"), Token);
        }

        var beforeBytes = new FileInfo(_database.Path).Length;
        Assert.True(beforeBytes > 1_000_000, $"expected a large file to compact, saw {beforeBytes} bytes");

        var deleted = await _history.DeleteAllAsync(Token);
        Assert.Equal(300, deleted);
        Assert.Equal(0, await _history.CountAsync(Token));

        var afterBytes = new FileInfo(_database.Path).Length;
        Assert.True(afterBytes < beforeBytes / 4,
            $"expected the file to shrink after VACUUM: {beforeBytes} -> {afterBytes} bytes");

        _database.Dispose();
        using var connection = OpenRaw();

        // Nothing but the migration ledger survives. That row has to stay: a database with no
        // schema record is a database that re-runs every migration on the next launch.
        foreach (var table in Tables(connection))
        {
            var rows = CountRows(connection, table);
            if (table == "schema_migrations")
            {
                Assert.True(rows > 0);
                continue;
            }

            Assert.Equal(0, rows);
        }

        using var freelist = connection.CreateCommand();
        freelist.CommandText = "PRAGMA freelist_count;";
        Assert.Equal(0L, Convert.ToInt64(freelist.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ReinjectTargetsTheIdentityRecordedWithTheEntry()
    {
        var target = Window("code", handle: 0xBEEF, pid: 9001);
        var stored = await _history.AppendAsync(Draft("hello", "Hello.", target), Token);

        var reloaded = await _history.GetAsync(stored.Id, Token);

        Assert.NotNull(reloaded);
        Assert.Equal(target, reloaded.Target);
        Assert.Equal(ReinjectCheck.Ready, reloaded.CheckReinject(target));
    }

    [Fact]
    public async Task ReinjectIntoADeadWindowIsDetectable()
    {
        var target = Window("code", handle: 0xBEEF, pid: 9001);
        var stored = await _history.AppendAsync(Draft("hello", "Hello.", target), Token);
        var reloaded = await _history.GetAsync(stored.Id, Token);
        Assert.NotNull(reloaded);

        // The window is gone: the focus tracker reports nothing at all.
        Assert.Equal(ReinjectCheck.WindowGone, reloaded.CheckReinject(TargetWindow.None));

        // The HWND was recycled by a different process. Same handle, different owner -- this is
        // precisely why the process id and name are recorded alongside it.
        var recycled = new TargetWindow(0xBEEF, 12345, "chrome", "Chrome_WidgetWin_1", "Bank");
        Assert.Equal(ReinjectCheck.DifferentWindow, reloaded.CheckReinject(recycled));

        // Same process, different window.
        var otherWindow = target with { Handle = 0xCAFE };
        Assert.Equal(ReinjectCheck.DifferentWindow, reloaded.CheckReinject(otherWindow));
    }

    [Fact]
    public async Task AnEntryWithNoRecordedTargetCannotBeReinjected()
    {
        var stored = await _history.AppendAsync(Draft("hello", "Hello.", TargetWindow.None), Token);
        var reloaded = await _history.GetAsync(stored.Id, Token);

        Assert.NotNull(reloaded);
        Assert.Equal(ReinjectCheck.NoRecordedTarget, reloaded.CheckReinject(Window("notepad")));
    }

    [Fact]
    public async Task RecentIsNewestFirstAndRespectsTheLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _history.AppendAsync(Draft($"raw {i}", $"final {i}"), Token);
        }

        var rows = await _history.RecentAsync(3, Token);

        Assert.Equal(3, rows.Count);
        Assert.Equal("final 4", rows[0].FinalText);
        Assert.Equal("final 2", rows[2].FinalText);
    }

    [Fact]
    public async Task ChangedFiresOnlyWhenTheDataActuallyChanged()
    {
        var changes = 0;
        _history.Changed += (_, _) => changes++;

        var first = await _history.AppendAsync(Draft("hello", "Hello."), Token);
        await _history.AppendAsync(Draft("goodbye", "Goodbye."), Token);
        await _history.DeleteAsync(first.Id, Token);
        await _history.DeleteAsync(first.Id, Token);   // already gone -- nothing changed
        await _history.DeleteAllAsync(Token);
        await _history.DeleteAllAsync(Token);          // already empty -- nothing changed

        Assert.Equal(4, changes);
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={Paths.Database};Pooling=False");
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<string> Tables(SqliteConnection connection)
    {
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

    private static IReadOnlyList<(string Name, string Type)> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();

        var columns = new List<(string, string)>();
        while (reader.Read())
        {
            columns.Add((reader.GetString(1), reader.GetString(2)));
        }

        return columns;
    }

    private static long CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _database.Dispose();
        _root.Delete(recursive: true);
    }
}
