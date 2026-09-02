using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jane.Core.Abstractions;

namespace Jane.Llm;

/// <summary>
/// Talks to a supervised Ollama server over the native <c>/api/chat</c> surface.
/// </summary>
/// <remarks>
/// Native, not <c>/v1</c>: the OpenAI-compatible surface cannot set <c>num_ctx</c> or control
/// thinking per request, and Jane needs both. <see cref="OpenAiAdapter"/> keeps <c>/v1</c>
/// available behind the same interface so a llama.cpp-server swap stays a base-URL change.
///
/// Device selection is per request via <c>options.num_gpu</c>, verified on this machine
/// 2026-09-02: <c>num_gpu: 0</c> loads entirely to CPU and <c>/api/ps</c> reports
/// <c>size_vram: 0</c>, while an unqualified request on the same server loads to the GPU. One
/// server therefore serves both routes -- see plan.md P0-1.
/// </remarks>
public sealed class OllamaChatClient(HttpClient http) : ILlmClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, object>
        {
            ["num_ctx"] = request.NumCtx,
            ["temperature"] = request.Temperature,
        };

        // Absent means "server default", i.e. the GPU. Zero is the documented CPU pin.
        if (request.Device == LlmDevice.Cpu)
        {
            options["num_gpu"] = 0;
        }

        if (request.MaxTokens is { } max)
        {
            options["num_predict"] = max;
        }

        var payload = new ChatRequest(
            Model: request.Model,
            Messages:
            [
                new ChatMessage("system", request.SystemPrompt),
                new ChatMessage("user", request.UserPrompt),
            ],
            Stream: false,
            Think: request.Think,
            KeepAlive: request.KeepAlive,
            Options: options);

        using var response = await http.PostAsJsonAsync("/api/chat", payload, Json, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellationToken)
                   ?? throw new OllamaException("Ollama returned an empty /api/chat body.");

        return new LlmResponse(
            Text: body.Message?.Content ?? string.Empty,
            PromptTokens: body.PromptEvalCount,
            CompletionTokens: body.EvalCount,
            TotalDuration: TimeSpan.FromTicks(body.TotalDuration / 100),
            LoadDuration: TimeSpan.FromTicks(body.LoadDuration / 100));
    }

    public async Task<IReadOnlyList<LoadedModel>> ListLoadedAsync(CancellationToken cancellationToken)
    {
        var body = await http.GetFromJsonAsync<PsResponse>("/api/ps", Json, cancellationToken);
        if (body?.Models is null)
        {
            return [];
        }

        return body.Models
            .Select(m => new LoadedModel(m.Name, m.Size, m.SizeVram, m.ContextLength))
            .ToArray();
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        // keep_alive 0 with an empty message list is the documented immediate-unload shape.
        var payload = new ChatRequest(model, [], Stream: false, Think: null, KeepAlive: 0, Options: null);
        using var response = await http.PostAsJsonAsync("/api/chat", payload, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("/api/version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var body = await http.GetFromJsonAsync<VersionResponse>("/api/version", Json, cancellationToken);
            return body?.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Model tags the server has locally, per <c>/api/tags</c>.</summary>
    public async Task<IReadOnlyList<string>> ListAvailableAsync(CancellationToken cancellationToken)
    {
        var body = await http.GetFromJsonAsync<TagsResponse>("/api/tags", Json, cancellationToken);
        return body?.Models?.Select(m => m.Name).ToArray() ?? [];
    }

    private sealed record ChatRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        bool Stream,
        bool? Think,
        [property: JsonPropertyName("keep_alive")] object? KeepAlive,
        IReadOnlyDictionary<string, object>? Options);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatResponse(
        ChatMessage? Message,
        [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int EvalCount,
        [property: JsonPropertyName("total_duration")] long TotalDuration,
        [property: JsonPropertyName("load_duration")] long LoadDuration);

    private sealed record PsResponse(IReadOnlyList<PsModel>? Models);

    private sealed record PsModel(
        string Name,
        long Size,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("context_length")] int ContextLength);

    private sealed record TagsResponse(IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(string Name);

    private sealed record VersionResponse(string Version);
}

public sealed class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }

    public OllamaException(string message, Exception inner) : base(message, inner) { }

    public OllamaException() { }
}
