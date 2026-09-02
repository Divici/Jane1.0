using Jane.Core.Abstractions;

namespace Jane.Llm;

/// <summary>
/// Presents an <see cref="LlmSession"/> as a plain <see cref="ILlmClient"/>, with the route and
/// model chosen by the governor rather than by the caller.
/// </summary>
/// <remarks>
/// The formatting layer builds prompts; it has no business knowing whether a game is running. This
/// adapter is where those two concerns meet: the formatter asks for a completion, and the route
/// supplier -- ultimately <c>GpuGovernor</c> -- decides which model answers it and on what device.
/// <para>
/// A caller that reaches here on the <see cref="LlmRoute.Skip"/> route is a bug: the formatter
/// should never have been invoked at all. It throws rather than quietly loading a model onto a
/// contended GPU.
/// </para>
/// </remarks>
public sealed class SessionLlmClient(LlmSession session, Func<LlmRoute> route, OllamaChatClient client) : ILlmClient
{
    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var current = route();
        if (current == LlmRoute.Skip)
        {
            throw new InvalidOperationException(
                "The LLM was asked for a completion while the governor had routed to Skip. " +
                "The formatter must not be invoked on that route.");
        }

        return await session.CompleteAsync(current, request.SystemPrompt, request.UserPrompt, cancellationToken);
    }

    public Task<IReadOnlyList<LoadedModel>> ListLoadedAsync(CancellationToken cancellationToken) =>
        client.ListLoadedAsync(cancellationToken);

    public Task UnloadAsync(string model, CancellationToken cancellationToken) =>
        client.UnloadAsync(model, cancellationToken);

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) =>
        client.IsReachableAsync(cancellationToken);
}
