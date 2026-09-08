using System.Globalization;
using Jane.Core.Diagnostics;
using Jane.Core.Platform;

namespace Jane.Core.Tests;

/// <summary>
/// The log file that did not exist.
/// </summary>
/// <remarks>
/// <para>
/// Four field bugs were reported after a day of real use, and every one of them had to be
/// diagnosed by reading source, because Jane wrote nothing anywhere. <see cref="JanePaths.Logs"/>
/// had existed since Phase 1 and nothing had ever created the directory, while the startup
/// failure message told the user to "see the log for details".
/// </para>
/// <para>
/// So the bar here is not "a logger exists". It is: the directory appears without being asked
/// for, a line survives a crash (no buffering that outlives the call), a broken disk never takes
/// the app down with it, and the file cannot grow without bound on a machine Jane starts with
/// Windows every day.
/// </para>
/// </remarks>
public sealed class FileLogTests
{
    [Fact]
    public void TheDirectoryIsCreatedByTheFirstWrite()
    {
        // The whole reason there was no log: JanePaths.Logs pointed somewhere nothing created.
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "logs");

        using var log = new FileLog(directory);
        log.Write(LogLevel.Info, "startup", "Jane started.");

        Assert.True(Directory.Exists(directory));
        Assert.Single(Directory.GetFiles(directory, "*.log"));
    }

    [Fact]
    public void ALineCarriesTheTimestampLevelCategoryAndMessage()
    {
        using var temp = new TempDirectory();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 8, 14, 12, 33, 123, TimeSpan.Zero));

        using (var log = new FileLog(temp.Path, time: clock))
        {
            log.Write(LogLevel.Warning, "injection", "Clipboard restore ran late.");
        }

        var line = Assert.Single(ReadLines(temp.Path));
        Assert.StartsWith("2026-09-08T14:12:33.123", line, StringComparison.Ordinal);
        Assert.Contains(" WRN ", line, StringComparison.Ordinal);
        Assert.Contains("injection", line, StringComparison.Ordinal);
        Assert.Contains("Clipboard restore ran late.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsAreAppendedAsKeyValuePairsInTheOrderTheyWereAdded()
    {
        // Order matters for reading a dictation line by eye: target, then strategy, then counts.
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path))
        {
            log.Write(LogLevel.Info, "dictation", "Injected.", LogFields.New()
                .Add("target", "notepad")
                .Add("strategy", "Clipboard")
                .Add("chars", 412));
        }

        var line = Assert.Single(ReadLines(temp.Path));
        var fields = line[line.IndexOf("target=", StringComparison.Ordinal)..];
        Assert.Equal("target=notepad strategy=Clipboard chars=412", fields);
    }

    [Fact]
    public void AValueWithSpacesIsQuotedAndOneWithNewlinesIsEscaped()
    {
        // One entry must be one line, or grep stops working and a multi-line exception message
        // turns a log into something only a human can read.
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path))
        {
            log.Write(LogLevel.Error, "startup", "Failed.", LogFields.New()
                .Add("detail", "line one\r\nline two")
                .Add("window", "Untitled - Notepad"));
        }

        var line = Assert.Single(ReadLines(temp.Path));
        Assert.Contains(@"detail=""line one\nline two""", line, StringComparison.Ordinal);
        Assert.Contains(@"window=""Untitled - Notepad""", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullFieldIsWrittenAsADashRatherThanBeingDropped()
    {
        // "It was not set" and "nobody looked" read identically if the key disappears, and the
        // clipboard investigation turns on exactly that distinction.
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path))
        {
            log.Write(LogLevel.Info, "dictation", "Done.", LogFields.New().Add("llm", null));
        }

        Assert.Contains("llm=-", Assert.Single(ReadLines(temp.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersAndTimesAreWrittenInvariantlySoALogIsReadableOnAnyMachine()
    {
        using var temp = new TempDirectory();
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            using var log = new FileLog(temp.Path);
            log.Write(LogLevel.Info, "audio", "Captured.", LogFields.New()
                .Add("seconds", 1.5)
                .Add("open", TimeSpan.FromMilliseconds(312.5)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }

        var line = Assert.Single(ReadLines(temp.Path));
        Assert.Contains("seconds=1.5", line, StringComparison.Ordinal);
        Assert.Contains("open=312.5ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesBelowTheMinimumLevelAreNotWritten()
    {
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path, new FileLogOptions { Minimum = LogLevel.Info }))
        {
            log.Write(LogLevel.Debug, "hook", "Key event.");
            log.Write(LogLevel.Info, "hook", "Hook installed.");
        }

        Assert.Single(ReadLines(temp.Path));
    }

    [Fact]
    public void TheFileRollsOnceItPassesTheSizeCapAndOlderFilesAreKept()
    {
        // Jane starts with Windows and runs all day. An unbounded log is a bug report waiting to
        // happen on a small SSD.
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path, new FileLogOptions { MaxBytes = 512, MaxFiles = 3 }))
        {
            for (var i = 0; i < 60; i++)
            {
                log.Write(LogLevel.Info, "bulk", new string('x', 60));
            }
        }

        var files = Directory.GetFiles(temp.Path, "*.log");
        Assert.Equal(3, files.Length);
        Assert.Contains(files, f => Path.GetFileName(f) == "jane.log");
        Assert.Contains(files, f => Path.GetFileName(f) == "jane.1.log");
        Assert.Contains(files, f => Path.GetFileName(f) == "jane.2.log");
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 4096));
    }

    [Fact]
    public void TheNewestLinesAreInTheCurrentFileAfterARoll()
    {
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path, new FileLogOptions { MaxBytes = 256, MaxFiles = 2 }))
        {
            for (var i = 0; i < 20; i++)
            {
                log.Write(LogLevel.Info, "bulk", $"entry {i}");
            }

            log.Write(LogLevel.Info, "bulk", "the last one");
        }

        var current = File.ReadAllText(Path.Combine(temp.Path, "jane.log"));
        Assert.Contains("the last one", current, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteThatCannotReachTheDiskIsCountedRatherThanThrown()
    {
        // Logging is diagnostics. A full disk or a locked file must never be the reason a
        // dictation fails, so every path out of Write is a return.
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "jane.log");

        using var log = new FileLog(temp.Path);
        log.Write(LogLevel.Info, "startup", "first");

        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write(LogLevel.Info, "startup", "blocked");
        }

        Assert.Equal(1, log.DroppedEntries);

        log.Write(LogLevel.Info, "startup", "after");
        Assert.Contains("after", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLineSurvivesWhenManyThreadsWriteAtOnce()
    {
        // The hook pump, the pipeline task and the UI thread all log. Interleaved bytes would
        // corrupt exactly the lines an investigation needs.
        using var temp = new TempDirectory();

        using (var log = new FileLog(temp.Path, new FileLogOptions { MaxBytes = 1024 * 1024 }))
        {
            Parallel.For(0, 200, i => log.Write(LogLevel.Info, "race", $"entry-{i:D3}"));
        }

        var lines = ReadLines(temp.Path);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.Contains("entry-", line, StringComparison.Ordinal));
    }

    [Fact]
    public void TheNullLogAcceptsEverythingAndWritesNothing()
    {
        // Every construction seam that has no path -- unit tests, the bench harness -- needs a
        // sink that is not a null check at each call site.
        NullLog.Instance.Write(LogLevel.Error, "startup", "Failed.", LogFields.New().Add("a", 1));
    }

    [Fact]
    public void TheCurrentFilePathIsExposedSoTheTrayCanOpenIt()
    {
        using var temp = new TempDirectory();
        using var log = new FileLog(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "jane.log"), log.CurrentFile);
        Assert.Equal(temp.Path, log.Directory);
    }

    private static string[] ReadLines(string directory) =>
        File.ReadAllLines(Path.Combine(directory, "jane.log"));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

/// <summary>A temp directory that deletes itself, so a failed assert leaves nothing behind.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "jane-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test holding a handle open is not a test failure.
        }
    }
}
