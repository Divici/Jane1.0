using System.Diagnostics;
using System.Globalization;

namespace Jane.Llm;

/// <param name="ExePath">Standalone ollama.exe from the official release archive, not the winget desktop package.</param>
/// <param name="Host">Bound loopback-only. Jane never listens on a routable address.</param>
/// <param name="MaxLoadedModels">
/// Two: the GPU-routed formatting model and the CPU-routed fallback can be resident at once,
/// which is what lets a single server serve both routes (plan.md P0-1).
/// </param>
/// <param name="AdoptOnly">
/// Never start a child; use a server the user already runs, or fail saying so.
/// </param>
/// <remarks>
/// <see cref="AdoptOnly"/> backs the opt-in "use my own Ollama" setting. Without it, pointing Jane
/// at port 11434 and finding nothing there would start a second server on the desktop app's own
/// port -- which is the one thing that arrangement must never do.
/// </remarks>
public sealed record OllamaSupervisorOptions(
    string ExePath,
    string Host = "127.0.0.1:11435",
    string KeepAlive = "180s",
    int MaxLoadedModels = 2,
    TimeSpan? StartupTimeout = null,
    bool AdoptOnly = false)
{
    public string BaseUrl => $"http://{Host}";

    public TimeSpan ResolvedStartupTimeout => StartupTimeout ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Owns the lifetime of Jane's single Ollama child process.
/// </summary>
/// <remarks>
/// One instance, not two. Ollama's device placement was believed to be fixed at server start,
/// which would have forced a second CPU-pinned server; probing on 2026-09-02 showed per-request
/// <c>options.num_gpu: 0</c> pins a model to the CPU and <c>/api/ps</c> confirms
/// <c>size_vram == 0</c>, with a CPU-resident and a GPU-resident model held simultaneously by
/// the same server. See plan.md P0-1.
///
/// The child is bound into a <see cref="ProcessJail"/> before anything else happens, so a Jane
/// crash cannot leave an orphan holding VRAM.
/// </remarks>
public sealed class OllamaSupervisor(OllamaSupervisorOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _child;
    private ProcessJail? _jail;
    private bool _disposed;

    public OllamaSupervisorOptions Options { get; } = options;

    public string BaseUrl => Options.BaseUrl;

    /// <summary>True when this supervisor started the server, false when it adopted an existing one.</summary>
    public bool OwnsChild => _child is { HasExited: false };

    public int? ChildProcessId => _child is { HasExited: false } ? _child.Id : null;

    /// <summary>
    /// Starts a child unless something already answers on the configured host, in which case the
    /// existing server is adopted -- typically a developer's own `ollama serve`, or a second Jane.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(OllamaChatClient client, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await client.IsReachableAsync(cancellationToken))
            {
                return false;
            }

            if (Options.AdoptOnly)
            {
                throw new OllamaException(
                    $"Nothing is listening on {Options.Host}. Jane is set to use an Ollama you run yourself, so it will not start one -- start Ollama, or turn that setting off and let Jane supervise its own copy.");
            }

            if (!File.Exists(Options.ExePath))
            {
                throw new OllamaException(
                    $"The model runtime is not installed: {Options.ExePath} does not exist. Download it from Jane's settings, under Models.");
            }

            var startInfo = new ProcessStartInfo(Options.ExePath, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.Environment["OLLAMA_HOST"] = Options.Host;
            startInfo.Environment["OLLAMA_KEEP_ALIVE"] = Options.KeepAlive;
            startInfo.Environment["OLLAMA_MAX_LOADED_MODELS"] =
                Options.MaxLoadedModels.ToString(CultureInfo.InvariantCulture);

            // Jane is single-user and one dictation at a time; parallel slots would multiply
            // VRAM by the context length for no benefit.
            startInfo.Environment["OLLAMA_NUM_PARALLEL"] = "1";

            var child = Process.Start(startInfo)
                        ?? throw new OllamaException($"Failed to start {Options.ExePath}.");

            // Drain both pipes. A child whose stdout fills its buffer blocks forever, and
            // ollama serve is chatty.
            child.OutputDataReceived += static (_, _) => { };
            child.ErrorDataReceived += static (_, _) => { };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();

            if (OperatingSystem.IsWindows())
            {
                _jail = new ProcessJail($"Jane.Ollama.{Environment.ProcessId}");
                if (!_jail.Assign(child.Handle))
                {
                    // Not fatal -- DisposeAsync still kills the child on a clean exit. It only
                    // means a hard crash could orphan it, which the doctor report will show.
                    _jail.Dispose();
                    _jail = null;
                }
            }

            _child = child;

            await WaitUntilReachableAsync(client, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WaitUntilReachableAsync(OllamaChatClient client, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Options.ResolvedStartupTimeout)
        {
            if (_child is { HasExited: true })
            {
                throw new OllamaException(
                    $"ollama.exe exited with code {_child.ExitCode} during startup. Another server may already own {Options.Host}.");
            }

            if (await client.IsReachableAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new OllamaException(
            $"ollama.exe did not answer on {Options.Host} within {Options.ResolvedStartupTimeout.TotalSeconds:F0}s.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var child = _child;
        _child = null;

        if (child is not null)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException)
            {
                // The jail below is the backstop; a child that will not die politely dies here.
            }
            finally
            {
                child.Dispose();
            }
        }

        if (OperatingSystem.IsWindows())
        {
            _jail?.Dispose();
            _jail = null;
        }
        _gate.Dispose();
    }
}
