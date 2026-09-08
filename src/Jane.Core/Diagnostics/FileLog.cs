using System.Globalization;
using System.Text;

namespace Jane.Core.Diagnostics;

/// <summary>How much a line matters. Anything below <see cref="FileLogOptions.Minimum"/> is dropped.</summary>
public enum LogLevel
{
    /// <summary>Per-key and per-frame detail. Off unless someone is actively chasing something.</summary>
    Debug,

    /// <summary>The normal record: one line per dictation, one per lifecycle event.</summary>
    Info,

    /// <summary>Something recovered, but the recovery is itself worth knowing about.</summary>
    Warning,

    /// <summary>Something the user noticed, or would have if they had been looking.</summary>
    Error,
}

/// <summary>
/// Key/value pairs appended to the end of a log line, in the order they were added.
/// </summary>
/// <remarks>
/// A dictation line carries a dozen values -- target, strategy, character count, what the modifier
/// gate saw, how long the paste took to settle. Formatting them into the message by hand produces
/// a different shape at every call site and nothing greppable; this keeps them uniform and
/// culture-invariant without pulling a logging framework into <c>Jane.Core</c>.
/// </remarks>
public sealed class LogFields
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    public static LogFields New() => new();

    public LogFields Add(string name, object? value)
    {
        _items.Add(new KeyValuePair<string, string>(name, Format(value)));
        return this;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    /// <summary>
    /// Renders one value.
    /// </summary>
    /// <remarks>
    /// Invariant everywhere. A log read on a machine with a comma decimal separator, or mailed to
    /// someone whose machine has one, has to say the same thing -- and <c>312,5ms</c> next to a
    /// comma-separated field list is genuinely ambiguous rather than merely ugly.
    /// </remarks>
    private static string Format(object? value) => value switch
    {
        null => "-",
        bool flag => flag ? "true" : "false",
        TimeSpan span => span.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms",
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        float number => number.ToString("0.###", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        Enum name => name.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };
}

/// <param name="MaxBytes">
/// When the current file passes this, it rolls. Jane starts with Windows and runs all day, so
/// "it is only a log" is exactly how a machine ends up with a gigabyte of them.
/// </param>
/// <param name="MaxFiles">How many files are kept, current one included.</param>
/// <param name="Minimum">The quietest level that still reaches disk.</param>
public sealed record FileLogOptions
{
    public long MaxBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxFiles { get; init; } = 5;

    public LogLevel Minimum { get; init; } = LogLevel.Info;
}

/// <summary>Somewhere to write a diagnostic line. One implementation writes; one throws it away.</summary>
public interface IJaneLog
{
    void Write(LogLevel level, string category, string message, LogFields? fields = null);
}

/// <summary>The sink for code paths with no log -- unit tests, the bench harness, `jane route`.</summary>
/// <remarks>
/// A real object rather than a nullable field, so no call site has to decide whether logging is
/// configured before saying what happened.
/// </remarks>
public sealed class NullLog : IJaneLog
{
    public static NullLog Instance { get; } = new();

    private NullLog()
    {
    }

    public void Write(LogLevel level, string category, string message, LogFields? fields = null)
    {
    }
}

/// <summary>
/// Jane's log file.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small: one rolling text file, one line per event, no framework. The alternative
/// considered was <c>Microsoft.Extensions.Logging</c>, which would have brought a provider model
/// and a configuration system into <c>Jane.Core</c> -- a project that is otherwise one data
/// dependency wide, on purpose, so it can stay off the Windows target framework.
/// </para>
/// <para>
/// The file is opened and closed around every write rather than held. That costs a few hundred
/// microseconds per line, on the order of a dozen lines per dictation, and buys the two properties
/// that make a log worth having: a line is on disk the instant <see cref="Write"/> returns even if
/// the process is killed a moment later, and an external tool holding the file open blocks Jane's
/// logging rather than Jane's logging blocking the tool.
/// </para>
/// <para>
/// Nothing here throws. A log that can fail a dictation is worse than no log, so a full disk or a
/// locked file increments <see cref="DroppedEntries"/> and the caller never learns about it.
/// </para>
/// </remarks>
public sealed class FileLog : IJaneLog, IDisposable
{
    private const string FileName = "jane.log";

    private readonly Lock _gate = new();
    private readonly FileLogOptions _options;
    private readonly TimeProvider _time;
    private long _dropped;
    private bool _disposed;

    public FileLog(string directory, FileLogOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory = directory;
        _options = options ?? new FileLogOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Where the files live. Usually <c>JanePaths.Logs</c>.</summary>
    public string Directory { get; }

    /// <summary>The file being appended to right now. What the tray's "Open log folder" points at.</summary>
    public string CurrentFile => Path.Combine(Directory, FileName);

    /// <summary>Lines that never reached disk. Surfaced by `jane doctor` rather than swallowed.</summary>
    public long DroppedEntries => Interlocked.Read(ref _dropped);

    public void Write(LogLevel level, string category, string message, LogFields? fields = null)
    {
        if (_disposed || level < _options.Minimum)
        {
            return;
        }

        var line = Compose(level, category, message, fields);

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfNeeded(line.Length);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    private string Compose(LogLevel level, string category, string message, LogFields? fields)
    {
        var builder = new StringBuilder(256);

        builder.Append(_time.GetLocalNow().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        builder.Append(' ').Append(Abbreviate(level));
        builder.Append(' ').Append(Escape(category));
        builder.Append(' ').Append(Escape(message));

        if (fields is not null)
        {
            foreach (var (name, value) in fields.Items)
            {
                builder.Append(' ').Append(name).Append('=').Append(Quote(value));
            }
        }

        return builder.Append(Environment.NewLine).ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };

    /// <summary>One entry is one line. A stack trace that spans four defeats every tool that reads this.</summary>
    private static string Escape(string value) =>
        value.Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    private static string Quote(string value)
    {
        var escaped = Escape(value);

        var needsQuotes = escaped.Length == 0 ||
            escaped.Contains(' ', StringComparison.Ordinal) ||
            escaped.Contains('"', StringComparison.Ordinal);

        return needsQuotes
            ? string.Concat("\"", escaped.Replace("\"", "\\\"", StringComparison.Ordinal), "\"")
            : escaped;
    }

    /// <summary>
    /// Shifts <c>jane.log</c> to <c>jane.1.log</c>, and each numbered file one further along.
    /// </summary>
    /// <remarks>
    /// The roll happens <em>before</em> the line that would overflow, not after, so no file ever
    /// exceeds the cap. Called under the write lock.
    /// </remarks>
    private void RollIfNeeded(int incomingLength)
    {
        var current = new FileInfo(CurrentFile);
        if (!current.Exists || current.Length + incomingLength <= _options.MaxBytes)
        {
            return;
        }

        for (var index = _options.MaxFiles - 1; index >= 1; index--)
        {
            var source = index == 1 ? CurrentFile : Numbered(index - 1);
            if (!File.Exists(source))
            {
                continue;
            }

            // Overwriting is how the oldest file falls off the end: at the top of the loop the
            // destination is jane.{MaxFiles-1}.log, and whatever was there is the one being aged out.
            File.Move(source, Numbered(index), overwrite: true);
        }

        // MaxFiles of 1 means "one file, capped": there is nowhere to roll to, so the cap is kept
        // by starting over rather than by growing.
        if (_options.MaxFiles <= 1 && File.Exists(CurrentFile))
        {
            File.Delete(CurrentFile);
        }
    }

    private string Numbered(int index) =>
        Path.Combine(Directory, string.Create(CultureInfo.InvariantCulture, $"jane.{index}.log"));

    public void Dispose() => _disposed = true;
}
