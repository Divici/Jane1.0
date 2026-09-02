namespace Jane.Core.Abstractions;

/// <summary>Which device the LLM request must run on.</summary>
public enum LlmDevice
{
    /// <summary>Let the server place it -- in practice, the GPU.</summary>
    Gpu,

    /// <summary>Pin to CPU. Sent as <c>options.num_gpu = 0</c> and verified via <c>/api/ps</c>.</summary>
    Cpu,
}

/// <param name="Model">Server-side model name, e.g. <c>jane-qwen3-4b</c>.</param>
/// <param name="Device">GPU or CPU. Selected per request; see the Phase 6 note in plan.md.</param>
/// <param name="NumCtx">Context window. Must be sized for the worst-case Edit Mode prompt.</param>
/// <param name="KeepAlive">
/// How long the server holds the model after this request. Jane sends "180s", never "0" --
/// unloading on every request would re-pay the cold load on every single dictation. A separate
/// idle timer issues the one explicit unload.
/// </param>
/// <param name="Think">
/// Reasoning. Always false for Jane. Note this is only honoured by checkpoints whose template
/// respects it; the GPU route uses a dedicated non-thinking model instead. See plan.md P0-2.
/// </param>
public sealed record LlmRequest(
    string Model,
    string SystemPrompt,
    string UserPrompt,
    LlmDevice Device = LlmDevice.Gpu,
    int NumCtx = 8192,
    string KeepAlive = "180s",
    bool Think = false,
    double Temperature = 0.2,
    int? MaxTokens = null);

/// <param name="LoadDuration">Server-reported cold-load cost, separated from generation so the
/// cold and warm paths can be reported apart rather than averaged into a misleading middle.</param>
public sealed record LlmResponse(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan TotalDuration,
    TimeSpan LoadDuration);

/// <param name="SizeVramBytes">Zero means the model is resident on the CPU. This is the value
/// the CPU route is asserted on -- pinning is verified, never trusted.</param>
public sealed record LoadedModel(string Name, long SizeBytes, long SizeVramBytes, int ContextLength);

public interface ILlmClient
{
    Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken cancellationToken);

    /// <summary>What the server currently holds in memory, per <c>/api/ps</c>.</summary>
    Task<IReadOnlyList<LoadedModel>> ListLoadedAsync(CancellationToken cancellationToken);

    /// <summary>Issues a single explicit unload (<c>keep_alive: 0</c>) for one model.</summary>
    Task UnloadAsync(string model, CancellationToken cancellationToken);

    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
