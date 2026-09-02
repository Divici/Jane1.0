using System.Text.Json;
using System.Text.Json.Serialization;
using Jane.Core.Platform;

namespace Jane.Core.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c>, and tells the app when it changed underneath.
/// </summary>
/// <remarks>
/// Shared by `bench` and the app: `bench` writes the engine selection, the app picks it up on its
/// next read or on the file-change notification, so re-benching does not require a restart.
///
/// Writes are atomic (temp then move). A settings file truncated by a crash mid-write is a file
/// that loses the user's hotkey, their dictionary and their onboarding state at once.
/// </remarks>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private JaneSettings _cached = new();
    private bool _disposed;

    public SettingsStore(JanePaths paths)
    {
        Paths = paths;
        _cached = Read();
    }

    public JanePaths Paths { get; }

    public string Path => Paths.SettingsFile;

    /// <summary>Last value read. Cheap; the app reads this on every dictation.</summary>
    public JaneSettings Current => _cached;

    /// <summary>Raised when the file changed on disk, after the new value has been read.</summary>
    public event EventHandler<JaneSettings>? Changed;

    /// <summary>Re-reads from disk. Falls back to defaults for a missing or unreadable file.</summary>
    public JaneSettings Read()
    {
        if (!File.Exists(Path))
        {
            _cached = new JaneSettings();
            return _cached;
        }

        try
        {
            var json = File.ReadAllText(Path);
            _cached = JsonSerializer.Deserialize<JaneSettings>(json, Json) ?? new JaneSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file must not stop Jane from starting. Defaults are all valid,
            // and the next write repairs the file.
            _cached = new JaneSettings();
        }

        return _cached;
    }

    public async Task WriteAsync(JaneSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var json = JsonSerializer.Serialize(settings, Json);

            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, Path, overwrite: true);

            _cached = settings;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Applies a change to the current value and persists it.</summary>
    public Task UpdateAsync(Func<JaneSettings, JaneSettings> update, CancellationToken cancellationToken) =>
        WriteAsync(update(Read()), cancellationToken);

    /// <summary>
    /// Starts watching the file so a `bench` run in another process is picked up live.
    /// </summary>
    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher is not null)
        {
            return;
        }

        Directory.CreateDirectory(Paths.Root);
        _watcher = new FileSystemWatcher(Paths.Root, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // A single save raises several events, and the writer may still hold the handle when the
        // first arrives. One short retry loop beats a debounce timer for something this rare.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var settings = Read();
                Changed?.Invoke(this, settings);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _writeGate.Dispose();
    }
}
