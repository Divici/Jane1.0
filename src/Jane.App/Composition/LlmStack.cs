using System.IO;
using System.Net.Http;
using Jane.Core.Abstractions;
using Jane.Core.Settings;
using Jane.Llm;
using Jane.Windows.Gpu;

namespace Jane.App.Composition;

/// <summary>
/// The whole LLM side of Jane: the supervised child process, the governor that decides whether it
/// may be used, and the session that owns VRAM residency.
/// </summary>
/// <remarks>
/// Grouped because their lifetimes are one lifetime. The supervisor must outlive the session (a
/// session unloading into a dead server hangs), the governor must be consulted before the session
/// is touched at all, and all three must be torn down before Jane's process exits or an orphaned
/// child keeps holding VRAM.
/// </remarks>
public sealed class LlmStack : IAsyncDisposable
{
    private readonly OllamaSupervisor _supervisor;
    private readonly HttpClient _http;
    private readonly OllamaChatClient _client;
    private readonly OllamaModelPuller _puller;
    private LlmRoute _currentRoute = LlmRoute.Skip;
    private bool _disposed;

    private LlmStack(
        OllamaSupervisor supervisor,
        HttpClient http,
        OllamaChatClient client,
        LlmSession session,
        GpuGovernor governor)
    {
        _supervisor = supervisor;
        _http = http;
        _puller = new OllamaModelPuller(http);
        _client = client;
        Session = session;
        Governor = governor;

        // A hung unload is `ollama#9926`: the request succeeds and the VRAM never comes back,
        // with the child spinning a core. Marking the session GPU-degraded stops Jane routing
        // there again until it restarts, which is the honest response to a server in that state.
        Session.UnloadHung += (_, reason) => Governor.Policy.MarkGpuDegraded(reason);
    }

    public LlmSession Session { get; }

    public GpuGovernor Governor { get; }

    /// <summary>The route chosen at the last key-down. Read by the formatter's routing decorator.</summary>
    public LlmRoute CurrentRoute => _currentRoute;

    public string BaseUrl => _supervisor.BaseUrl;

    public static LlmStack Create(string ollamaExePath, JaneSettings settings)
    {
        var supervisor = new OllamaSupervisor(new OllamaSupervisorOptions(
            ollamaExePath,
            Host: HostFrom(settings),
            KeepAlive: settings.Llm.KeepAlive,
            AdoptOnly: settings.Llm.UseSystemOllama));

        var http = new HttpClient
        {
            BaseAddress = new Uri(supervisor.BaseUrl),

            // Generous, because a cold model load can take seconds and the formatter applies its
            // own, much tighter, per-request deadline on top of this.
            Timeout = TimeSpan.FromMinutes(2),
        };

        var client = new OllamaChatClient(http);
        var session = new LlmSession(client, new LlmSessionOptions
        {
            GpuModel = settings.Llm.GpuModel,
            CpuModel = settings.Llm.CpuModel,
            KeepAlive = settings.Llm.KeepAlive,
            IdleUnload = TimeSpan.FromSeconds(settings.Llm.IdleUnloadSeconds),
            NumCtx = settings.Llm.NumCtx,
            CpuNumThreads = settings.Llm.CpuNumThreads,
        });

        var governor = new GpuGovernor(settings.Gpu, settings.Llm.InGame, settings.Llm.Enabled);

        return new LlmStack(supervisor, http, client, session, governor);
    }

    /// <summary>Jane's own supervised port, or the desktop app's, when the user opted into theirs.</summary>
    public const string SupervisedHost = "127.0.0.1:11435";

    /// <summary>Ollama's default. Jane only ever talks to this when explicitly told to.</summary>
    public const string SystemHost = "127.0.0.1:11434";

    private static string HostFrom(JaneSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("JANE_OLLAMA_GPU_URL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(configured).Authority;
        }

        return settings.Llm.UseSystemOllama ? SystemHost : SupervisedHost;
    }

    /// <summary>
    /// Starts the supervised child if the LLM is enabled at all.
    /// </summary>
    /// <remarks>
    /// Failure here is not fatal. Jane still dictates: the governor's route falls back to Skip and
    /// raw Parakeet output is injected, which is the same path a running game takes.
    /// </remarks>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _supervisor.EnsureRunningAsync(_client, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or IOException)
        {
            Governor.Policy.MarkGpuDegraded($"Ollama could not be started: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Consulted at key-down: picks a route and starts loading the model concurrently with speech.
    /// </summary>
    public LlmRoute BeginDictation(CancellationToken cancellationToken)
    {
        Governor.Invalidate();
        var decision = Governor.Decide();
        _currentRoute = decision.Route;

        // Warm-up runs alongside the user speaking. A dictation takes seconds; a cold model load
        // takes one to three. Overlapping them is what keeps the cold path invisible.
        Session.BeginWarmup(decision.Route, cancellationToken);
        return decision.Route;
    }

    /// <summary>Builds the client the formatter talks to, with the route supplied by the governor.</summary>
    public ILlmClient CreateClient() => new SessionLlmClient(Session, () => _currentRoute, _client);

    /// <summary>Pulls models through the supervised server, so onboarding needs no CLI.</summary>
    public OllamaModelPuller Puller => _puller;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Order matters: give the VRAM back, then stop the server, then drop the socket. A
        // session unloading into an already-dead server would block until its timeout.
        await Session.DisposeAsync();
        await _supervisor.DisposeAsync();

        Governor.Dispose();
        _http.Dispose();
    }
}
