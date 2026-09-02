using System.Net;
using System.Text;
using System.Text.Json;
using Jane.Core.Abstractions;
using Jane.Llm;

namespace Jane.Llm.Tests;

public sealed class OllamaChatClientTests
{
    [Fact]
    public async Task CpuDevice_SendsNumGpuZero()
    {
        // This one field is the whole CPU route. Ollama's device placement was believed to be
        // fixed at server start; per-request num_gpu:0 is what collapsed Jane's two supervised
        // servers into one (plan.md P0-1), so a regression here silently re-introduces GPU
        // contention while the user is gaming.
        var handler = new CapturingHandler(ChatBody("OK"));
        var client = new OllamaChatClient(handler.CreateClient());

        await client.ChatAsync(
            new LlmRequest("jane-qwen3-1.7b", "system", "user", Device: LlmDevice.Cpu),
            TestContext.Current.CancellationToken);

        var options = handler.LastRequestJson!.RootElement.GetProperty("options");
        Assert.Equal(0, options.GetProperty("num_gpu").GetInt32());
    }

    [Fact]
    public async Task GpuDevice_OmitsNumGpuEntirely()
    {
        // Absent means "server default". Sending an explicit non-zero value would pin a layer
        // count Jane has no business choosing.
        var handler = new CapturingHandler(ChatBody("OK"));
        var client = new OllamaChatClient(handler.CreateClient());

        await client.ChatAsync(
            new LlmRequest("jane-qwen3-4b", "system", "user", Device: LlmDevice.Gpu),
            TestContext.Current.CancellationToken);

        var options = handler.LastRequestJson!.RootElement.GetProperty("options");
        Assert.False(options.TryGetProperty("num_gpu", out _));
    }

    [Fact]
    public async Task NumCtxAndKeepAlive_AreSentOnEveryRequest()
    {
        // num_ctx must be per-request: the /v1 surface cannot set it, which is why Jane uses the
        // native /api/chat. keep_alive must be a duration, never 0 -- unloading on every request
        // would re-pay the cold load on every single dictation.
        var handler = new CapturingHandler(ChatBody("OK"));
        var client = new OllamaChatClient(handler.CreateClient());

        await client.ChatAsync(
            new LlmRequest("jane-qwen3-4b", "system", "user", NumCtx: 12288, KeepAlive: "180s"),
            TestContext.Current.CancellationToken);

        var root = handler.LastRequestJson!.RootElement;
        Assert.Equal(12288, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("180s", root.GetProperty("keep_alive").GetString());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Unload_SendsKeepAliveZero()
    {
        var handler = new CapturingHandler(ChatBody(string.Empty));
        var client = new OllamaChatClient(handler.CreateClient());

        await client.UnloadAsync("jane-qwen3-4b", TestContext.Current.CancellationToken);

        var root = handler.LastRequestJson!.RootElement;
        Assert.Equal(0, root.GetProperty("keep_alive").GetInt32());
        Assert.Empty(root.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task ListLoaded_ReadsSizeVramSoPinningCanBeAsserted()
    {
        const string Ps = """
            {"models":[
              {"name":"jane-qwen3-1.7b:latest","model":"jane-qwen3-1.7b:latest","size":2419306003,"size_vram":0,"context_length":8192},
              {"name":"jane-qwen3-4b:latest","model":"jane-qwen3-4b:latest","size":3873366343,"size_vram":3873366343,"context_length":8192}
            ]}
            """;
        var handler = new CapturingHandler(Ps);
        var client = new OllamaChatClient(handler.CreateClient());

        var loaded = await client.ListLoadedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0, loaded[0].SizeVramBytes);
        Assert.Equal(3873366343, loaded[1].SizeVramBytes);
    }

    [Fact]
    public async Task IsReachable_ReturnsFalseRatherThanThrowingWhenNothingIsListening()
    {
        // The doctor and the supervisor both poll this in a loop; an exception per poll would
        // make startup a stack of first-chance noise.
        var handler = new CapturingHandler(string.Empty, HttpStatusCode.ServiceUnavailable)
        {
            Throw = new HttpRequestException("connection refused"),
        };
        var client = new OllamaChatClient(handler.CreateClient());

        Assert.False(await client.IsReachableAsync(TestContext.Current.CancellationToken));
    }

    private static string ChatBody(string content) =>
        $$"""
        {"model":"m","message":{"role":"assistant","content":"{{content}}"},
         "done":true,"prompt_eval_count":9,"eval_count":2,
         "total_duration":100000000,"load_duration":1000000}
        """;

    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public JsonDocument? LastRequestJson { get; private set; }

        public Exception? Throw { get; init; }

        public HttpClient CreateClient() =>
            new(this) { BaseAddress = new Uri("http://127.0.0.1:11435") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                LastRequestJson = JsonDocument.Parse(body);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
