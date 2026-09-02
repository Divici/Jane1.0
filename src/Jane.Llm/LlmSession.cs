using System.Diagnostics;
using Jane.Core.Abstractions;

namespace Jane.Llm;

/// <param name="IdleUnload">
/// How long after the last request Jane issues its single explicit unload. Deliberately longer
/// than a pause for thought and shorter than a coffee break.
/// </param>
/// <param name="UnloadGrace">
/// How long the watchdog waits for the model to actually leave memory before concluding the
/// unload has hung. `ollama#9926` reports the unload path spinning at 100% CPU when another
/// process holds VRAM -- which is exactly the situation while gaming, and burning a core is the
/// one thing this whole design exists to avoid.
/// </param>
public sealed record LlmSessionOptions
{
    public string GpuModel { get; init; } = "jane-qwen3-4b";

    public string CpuModel { get; init; } = "jane-qwen3-1.7b";

    public string KeepAlive { get; init; } = "180s";

    public TimeSpan IdleUnload { get; init; } = TimeSpan.FromSeconds(180);

    public TimeSpan UnloadGrace { get; init; } = TimeSpan.FromSeconds(20);

    public int NumCtx { get; init; } = 8192;

    /// <summary>Threads for the CPU route. Capped so a CPU-formatted dictation cannot take the machine.</summary>
    public int CpuNumThreads { get; init; } = 4;
}

/// <summary>
/// Owns VRAM residency: warms the routed model up, keeps it alive across a conversation, and
/// gives it back when the user stops.
/// </summary>
/// <remarks>
/// <para>
/// The locked promise is zero VRAM held when idle. The naive way to keep it -- <c>keep_alive: 0</c>
/// on every request -- would re-pay the multi-second cold load on *every single dictation*, which
/// is BLOCKER #6. So requests carry a real keep-alive and a single explicit unload fires on an
/// idle timer instead.
/// </para>
/// <para>
/// Exactly one unload, not one per timer tick. Ollama's unload path is the part with the open
/// hang bug; issuing it repeatedly against a wedged server would turn one stuck request into a
/// queue of them.
/// </para>
/// </remarks>
public sealed class LlmSession : IAsyncDisposable
{
    private readonly OllamaChatClient _client;
    private readonly LlmSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _idle;
    private string? _residentModel;
    private bool _unloadIssued;
    private bool _disposed;

    public LlmSession(OllamaChatClient client, LlmSessionOptions? options = null)
    {
        _client = client;
        _options = options ?? new LlmSessionOptions();
    }

    /// <summary>Raised when an unload did not take effect within the grace period.</summary>
    /// <remarks>The supervisor subscribes to this and restarts the child.</remarks>
    public event EventHandler<string>? UnloadHung;

    /// <summary>Number of explicit unloads actually sent. Asserted to be exactly one per idle period.</summary>
    public int UnloadCount { get; private set; }

    public string ModelFor(LlmRoute route) => route == LlmRoute.Cpu ? _options.CpuModel : _options.GpuModel;

    /// <summary>
    /// Starts loading the routed model without waiting for it.
    /// </summary>
    /// <remarks>
    /// Called at key-down, so the load runs concurrently with the user speaking. A dictation takes
    /// seconds; a cold load takes one to three. Overlapping them is the difference between the
    /// cold path being visible and being free.
    /// </remarks>
    public void BeginWarmup(LlmRoute route, CancellationToken cancellationToken)
    {
        if (route == LlmRoute.Skip || _disposed)
        {
            return;
        }

        CancelIdleTimer();

        _ = Task.Run(async () =>
        {
            try
            {
                // An empty prompt with a real keep_alive is the cheapest thing that forces a load:
                // it pays the model load without paying for generation.
                await _client.ChatAsync(
                    new LlmRequest(ModelFor(route), string.Empty, string.Empty,
                        Device: route == LlmRoute.Cpu ? LlmDevice.Cpu : LlmDevice.Gpu,
                        NumCtx: _options.NumCtx,
                        KeepAlive: _options.KeepAlive,
                        MaxTokens: 1),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or OllamaException or OperationCanceledException)
            {
                // A failed warm-up costs nothing: the real request will load the model itself, or
                // fail in a way the caller reports properly.
            }
        }, cancellationToken);
    }

    public async Task<LlmResponse> CompleteAsync(
        LlmRoute route, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (route == LlmRoute.Skip)
        {
            throw new InvalidOperationException("CompleteAsync must not be called on the Skip route.");
        }

        CancelIdleTimer();
        var model = ModelFor(route);

        var response = await _client.ChatAsync(
            new LlmRequest(model, systemPrompt, userPrompt,
                Device: route == LlmRoute.Cpu ? LlmDevice.Cpu : LlmDevice.Gpu,
                NumCtx: _options.NumCtx,
                KeepAlive: _options.KeepAlive),
            cancellationToken);

        _residentModel = model;
        _unloadIssued = false;

        if (route == LlmRoute.Cpu)
        {
            await AssertCpuPinnedAsync(model, cancellationToken);
        }

        StartIdleTimer();
        return response;
    }

    /// <summary>
    /// Confirms a CPU-routed model really is on the CPU.
    /// </summary>
    /// <remarks>
    /// Pinning is verified, never trusted. The whole point of the CPU route is to leave the GPU
    /// alone while the user is gaming; a "CPU" model that quietly loaded onto the GPU would be
    /// worse than not having the route at all, because the user believes they are protected.
    /// </remarks>
    private async Task AssertCpuPinnedAsync(string model, CancellationToken cancellationToken)
    {
        var loaded = await _client.ListLoadedAsync(cancellationToken);
        var entry = loaded.FirstOrDefault(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase));

        if (entry is { SizeVramBytes: > 0 })
        {
            throw new OllamaException(
                $"The CPU route was asked for but {model} holds {entry.SizeVramBytes / 1024 / 1024} MB of VRAM. " +
                "options.num_gpu=0 was ignored by this server, so the CPU route is not safe to use.");
        }
    }

    private void StartIdleTimer()
    {
        CancelIdleTimer();

        var cts = new CancellationTokenSource();
        _idle = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.IdleUnload, cts.Token);
                await UnloadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Another dictation arrived. The model stays where it is, which is the point.
            }
        }, cts.Token);
    }

    private void CancelIdleTimer()
    {
        var existing = Interlocked.Exchange(ref _idle, null);
        if (existing is null)
        {
            return;
        }

        existing.Cancel();
        existing.Dispose();
    }

    /// <summary>
    /// Issues the single explicit unload, then confirms the model actually left memory.
    /// </summary>
    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var model = _residentModel;
            if (model is null || _unloadIssued)
            {
                return;
            }

            _unloadIssued = true;
            UnloadCount++;

            try
            {
                await _client.UnloadAsync(model, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or OllamaException)
            {
                UnloadHung?.Invoke(this, $"The unload request for {model} failed: {ex.Message}");
                return;
            }

            // Verified, not assumed: `ollama#9926` describes the unload path hanging rather than
            // erroring, so a successful HTTP response is not evidence the VRAM came back.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < _options.UnloadGrace)
            {
                var loaded = await _client.ListLoadedAsync(cancellationToken);
                if (!loaded.Any(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase)))
                {
                    _residentModel = null;
                    return;
                }

                await Task.Delay(500, cancellationToken);
            }

            UnloadHung?.Invoke(this,
                $"{model} is still loaded {_options.UnloadGrace.TotalSeconds:F0}s after an unload was issued.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelIdleTimer();

        // Give VRAM back on the way out rather than relying on the server's own keep-alive. A
        // user who quits Jane expects the memory back immediately.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await UnloadAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or OllamaException)
        {
            // The supervisor's Job Object is the backstop: killing the server frees the VRAM.
        }

        _gate.Dispose();
    }
}
