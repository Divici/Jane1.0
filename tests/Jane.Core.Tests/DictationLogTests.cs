using Jane.Core.Abstractions;
using Jane.Core.Diagnostics;

namespace Jane.Core.Tests;

/// <summary>
/// One line per dictation, with enough on it to diagnose the next field report without reading
/// source.
/// </summary>
/// <remarks>
/// <para>
/// The four bugs reported on 2026-09-08 were all diagnosed by inspection, because Jane recorded
/// nothing. The history table holds what Jane produced -- transcript, final text, whether it was
/// injected -- and none of what it did: which strategy was chosen and why, what the modifier gate
/// saw, how many <c>INPUT</c> records Windows actually accepted, how long the paste took to land.
/// Those are exactly the values that separate "the clipboard was restored too early" from "a
/// modifier was left held", and both produce the same symptom on screen.
/// </para>
/// <para>
/// So the log line is written from the orchestrator, once, on every terminal path including the
/// failing ones -- a dictation that produced nothing is the case most worth explaining.
/// </para>
/// </remarks>
public sealed class DictationLogTests
{
    [Fact]
    public async Task ASuccessfulDictationWritesOneLineNamingTheTargetAndTheStrategy()
    {
        var log = new RecordingLog();
        var harness = new Harness(log: log);

        await harness.DictateAsync("hello world");

        var entry = Assert.Single(log.Entries, e => e.Category == "dictation");
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("notepad", entry.Field("app"));
        Assert.Equal("Notepad", entry.Field("class"));
        Assert.Equal("Unicode", entry.Field("strategy"));
        Assert.Equal("ok", entry.Field("result"));
    }

    [Fact]
    public async Task TheLineCarriesTheCharacterCountAudioLengthAndHowLongEachStageTook()
    {
        var log = new RecordingLog();
        var harness = new Harness(log: log);

        await harness.DictateAsync("hello world");

        var entry = Assert.Single(log.Entries, e => e.Category == "dictation");
        Assert.Equal("11", entry.Field("chars"));

        // The fake source returns one second of tone with 500 ms of it pre-roll.
        Assert.Equal("1000ms", entry.Field("audio"));
        Assert.Equal("500ms", entry.Field("preroll"));
        Assert.NotNull(entry.Field("asr"));
        Assert.NotNull(entry.Field("inject"));
        Assert.NotNull(entry.Field("total"));
    }

    [Fact]
    public async Task WhatTheStrategyMeasuredIsCarriedThroughRatherThanSummarised()
    {
        // The point of the exercise: SendInput's accepted/total, what the modifier gate saw, and
        // whether the clipboard came back are the numbers that name the bug.
        var log = new RecordingLog();
        var harness = new Harness(log: log);
        harness.Injector.Result = new InjectionResult(true, InjectionStrategy.Clipboard, 412, TimeSpan.FromMilliseconds(90))
        {
            Diagnostics = new InjectionDiagnostics
            {
                RecordsSent = 4,
                RecordsAccepted = 4,
                ModifiersInitiallyHeld = "Right Ctrl",
                ModifiersStillHeld = null,
                ModifierWait = TimeSpan.FromMilliseconds(42),
                PasteSettle = TimeSpan.FromMilliseconds(180),
                PasteSettleReason = "target-changed",
                ClipboardRestored = true,
            },
        };

        await harness.DictateAsync("hello world");

        var entry = Assert.Single(log.Entries, e => e.Category == "dictation");
        Assert.Equal("Clipboard", entry.Field("strategy"));
        Assert.Equal("4/4", entry.Field("records"));
        Assert.Equal("Right Ctrl", entry.Field("mods-at-start"));
        Assert.Equal("-", entry.Field("mods-held"));
        Assert.Equal("42ms", entry.Field("mods-wait"));
        Assert.Equal("180ms", entry.Field("settle"));
        Assert.Equal("target-changed", entry.Field("settle-why"));
        Assert.Equal("true", entry.Field("clip-restored"));
    }

    [Fact]
    public async Task AFailedInjectionIsLoggedAsAWarningWithTheFailureAndItsDetail()
    {
        var log = new RecordingLog();
        var harness = new Harness(log: log);
        harness.Injector.Result = InjectionResult.Aborted(
            InjectionFailure.ModifierHeld, "Still held after 500 ms: Right Ctrl.");

        await harness.DictateAsync("hello world");

        var entry = Assert.Single(log.Entries, e => e.Category == "dictation");
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("ModifierHeld", entry.Field("result"));
        Assert.Contains("Right Ctrl", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADictationThatHeardNothingIsStillLogged()
    {
        // The most common "Jane did nothing" report, and the one with no history row to inspect.
        var log = new RecordingLog();
        var harness = new Harness(log: log) { SpeechDetected = false };

        await harness.DictateAsync();

        var entry = Assert.Single(log.Entries, e => e.Category == "dictation");
        Assert.Equal("NoSpeech", entry.Field("result"));
        Assert.Equal("1000ms", entry.Field("audio"));
    }

    [Fact]
    public async Task TheTranscriptItselfIsNeverWrittenToTheLog()
    {
        // A log is a file that gets mailed to someone. Dictated text is the most private thing
        // Jane touches, it is already in the history database the user can clear, and a length is
        // enough to diagnose an injection.
        var log = new RecordingLog();
        var harness = new Harness(log: log);

        await harness.DictateAsync("my password is hunter2");

        Assert.All(log.Entries, entry =>
        {
            Assert.DoesNotContain("hunter2", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.All(entry.Fields, field =>
                Assert.DoesNotContain("hunter2", field.Value, StringComparison.OrdinalIgnoreCase));
        });
    }
}

/// <summary>An <see cref="IJaneLog"/> that keeps entries in memory, so a test can read fields back.</summary>
internal sealed class RecordingLog : IJaneLog
{
    private readonly List<RecordedEntry> _entries = [];

    public IReadOnlyList<RecordedEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Write(LogLevel level, string category, string message, LogFields? fields = null)
    {
        lock (_entries)
        {
            _entries.Add(new RecordedEntry(level, category, message, fields?.Items ?? []));
        }
    }
}

internal sealed record RecordedEntry(
    LogLevel Level,
    string Category,
    string Message,
    IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public string? Field(string name) =>
        Fields.Where(f => f.Key == name).Select(f => f.Value).FirstOrDefault();
}
