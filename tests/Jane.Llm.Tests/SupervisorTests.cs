using System.Net;
using System.Text;
using System.Text.Json;
using Jane.Core.Abstractions;
using Jane.Llm;

namespace Jane.Llm.Tests;

/// <summary>
/// VRAM residency and process lifetime -- the two promises that make "leave all my resources
/// free" true or false.
/// </summary>
public sealed class SupervisorTests
{
    [Fact]
    public async Task IdleTimerIssuesExactlyOneUnload()
    {
        // Exactly one, not one per tick. Ollama's unload path is the part with the open hang bug
        // (`ollama#9926`); issuing it repeatedly at a wedged server turns one stuck request into
        // a queue of them.
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromMilliseconds(150),
            UnloadGrace = TimeSpan.FromSeconds(2),
        });

        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => session.UnloadCount > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.UnloadCount);
        Assert.Empty(await server.Client.ListLoadedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestsUseARealKeepAliveRatherThanUnloadingEveryTime()
    {
        // BLOCKER #6: keep_alive 0 per request would re-pay the cold load on every dictation.
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromHours(1),
        });

        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);

        var keepAlive = server.LastChatRequest!.RootElement.GetProperty("keep_alive");
        Assert.Equal(JsonValueKind.String, keepAlive.ValueKind);
        Assert.Equal("180s", keepAlive.GetString());
        Assert.Equal(0, session.UnloadCount);
    }

    [Fact]
    public async Task AnotherDictationBeforeTheTimerCancelsTheUnload()
    {
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromMilliseconds(400),
        });

        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        // The first timer must not fire after the second request reset it, or a user dictating
        // steadily would keep losing the model mid-conversation.
        Assert.Equal(0, session.UnloadCount);
    }

    [Fact]
    public async Task HungUnloadIsReportedSoTheChildCanBeRestarted()
    {
        // The model stays listed in /api/ps forever, which is exactly the `ollama#9926` shape:
        // the HTTP call succeeds and the memory never comes back.
        var server = new FakeServer { IgnoreUnload = true };
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromHours(1),
            UnloadGrace = TimeSpan.FromMilliseconds(600),
        });

        string? hung = null;
        session.UnloadHung += (_, reason) => hung = reason;

        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);
        await session.UnloadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(hung);
        Assert.Contains("still loaded", hung, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CpuInstanceAssertsZeroVram()
    {
        // A "CPU" model that quietly loaded onto the GPU is worse than having no CPU route,
        // because the user believes they are protected while Jane contends for the card.
        var server = new FakeServer { CpuModelHoldsVram = 2_000_000_000 };
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromHours(1),
        });

        var ex = await Assert.ThrowsAsync<OllamaException>(() =>
            session.CompleteAsync(LlmRoute.Cpu, "system", "user", TestContext.Current.CancellationToken));

        Assert.Contains("num_gpu=0 was ignored", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CpuRouteSendsNumGpuZeroAndPassesWhenTheServerHonoursIt()
    {
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromHours(1),
        });

        await session.CompleteAsync(LlmRoute.Cpu, "system", "user", TestContext.Current.CancellationToken);

        var options = server.LastChatRequest!.RootElement.GetProperty("options");
        Assert.Equal(0, options.GetProperty("num_gpu").GetInt32());
    }

    [Fact]
    public async Task WarmupLoadsTheRoutedModelWithoutBlocking()
    {
        // Fired at key-down so the load overlaps with the user speaking. If it blocked, it would
        // put the cold load back on the path it exists to hide.
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            IdleUnload = TimeSpan.FromHours(1),
        });

        session.BeginWarmup(LlmRoute.Gpu, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => server.ChatCount > 0, TimeSpan.FromSeconds(5));

        Assert.True(server.ChatCount > 0);
    }

    [Fact]
    public async Task WarmupOnTheSkipRouteDoesNothingAtAll()
    {
        // While a game is running the whole point is to touch nothing. A warm-up here would load
        // a model onto a contended GPU for a request that is never going to be made.
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client);

        session.BeginWarmup(LlmRoute.Skip, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(0, server.ChatCount);
    }

    [Fact]
    public async Task MaxSizeEditPromptRoundTripsUntruncated()
    {
        // Aqua caps Edit Mode selections at 6,000 characters, so num_ctx has to hold that plus the
        // instruction and the response. A truncated prompt would silently rewrite half a document.
        var server = new FakeServer();
        await using var session = new LlmSession(server.Client, new LlmSessionOptions
        {
            NumCtx = 8192,
            IdleUnload = TimeSpan.FromHours(1),
        });

        var selection = new string('x', 6000);
        await session.CompleteAsync(LlmRoute.Gpu, "system", selection, TestContext.Current.CancellationToken);

        var messages = server.LastChatRequest!.RootElement.GetProperty("messages");
        var sent = messages[1].GetProperty("content").GetString();

        Assert.Equal(6000, sent!.Length);
        Assert.Equal(8192, server.LastChatRequest.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task DisposeGivesVramBackImmediately()
    {
        var server = new FakeServer();
        var session = new LlmSession(server.Client, new LlmSessionOptions { IdleUnload = TimeSpan.FromHours(1) });

        await session.CompleteAsync(LlmRoute.Gpu, "system", "user", TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        // A user who quits Jane expects the memory back now, not in three minutes.
        Assert.Equal(1, session.UnloadCount);
        Assert.Empty(await server.Client.ListLoadedAsync(TestContext.Current.CancellationToken));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>An in-memory Ollama that tracks what is loaded, so residency can be asserted.</summary>
    private sealed class FakeServer
    {
        private readonly Dictionary<string, long> _loaded = [];

        public FakeServer() => Client = new OllamaChatClient(new HttpClient(new Handler(this))
        {
            BaseAddress = new Uri("http://127.0.0.1:11435"),
        });

        public OllamaChatClient Client { get; }

        public JsonDocument? LastChatRequest { get; private set; }

        public int ChatCount { get; private set; }

        /// <summary>Reproduces `ollama#9926`: the unload succeeds and the model never leaves.</summary>
        public bool IgnoreUnload { get; init; }

        /// <summary>Reproduces a server that ignores <c>num_gpu: 0</c>.</summary>
        public long CpuModelHoldsVram { get; init; }

        private sealed class Handler(FakeServer server) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;

                if (path == "/api/ps")
                {
                    var models = string.Join(",", server._loaded.Select(kv =>
                        $$"""{"name":"{{kv.Key}}","model":"{{kv.Key}}","size":1000,"size_vram":{{kv.Value}},"context_length":8192}"""));
                    return Json($$"""{"models":[{{models}}]}""");
                }

                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var json = JsonDocument.Parse(body);
                server.LastChatRequest = json;

                var model = json.RootElement.GetProperty("model").GetString()!;
                var keepAlive = json.RootElement.GetProperty("keep_alive");
                var isUnload = keepAlive.ValueKind == JsonValueKind.Number && keepAlive.GetInt32() == 0;

                if (isUnload)
                {
                    if (!server.IgnoreUnload)
                    {
                        server._loaded.Remove(model);
                    }

                    return Json("""{"model":"m","done":true,"prompt_eval_count":0,"eval_count":0,"total_duration":1,"load_duration":1}""");
                }

                server.ChatCount++;

                var cpu = json.RootElement.GetProperty("options").TryGetProperty("num_gpu", out var numGpu)
                          && numGpu.GetInt32() == 0;
                server._loaded[model] = cpu ? server.CpuModelHoldsVram : 3_000_000_000;

                return Json("""{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":9,"eval_count":2,"total_duration":100000000,"load_duration":1000000}""");
            }

            private static HttpResponseMessage Json(string content) =>
                new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
