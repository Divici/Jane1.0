using Jane.Core.Abstractions;

namespace Jane.Core.Formatting;

/// <summary>
/// Token-by-token generation, for transports that support it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ILlmClient"/> is fixed at Phase 6 and exposes only a whole-response call, so this is
/// an optional extra a transport may also implement; <see cref="TranscriptFormatter"/> uses it
/// when present and falls back to <see cref="ILlmClient.ChatAsync"/> when not. It belongs beside
/// <c>ILlmClient</c> in <c>Jane.Core.Abstractions</c> and should move there once a shipped
/// transport implements it.
/// </para>
/// <para>
/// The point is not to show tokens as they arrive -- nothing in the UI wants that, and the
/// overlay shows one "thinking" state for the whole stage. It is that a runaway generation is
/// detectable long before it finishes: once the accumulated text passes
/// <see cref="OutputValidator.MaxAcceptedLength"/> the answer will be discarded whatever else
/// arrives, so the dictation can fall back to raw text immediately instead of spending the rest
/// of the timeout budget on a poem nobody will see.
/// </para>
/// </remarks>
public interface IStreamingLlmClient
{
    IAsyncEnumerable<string> ChatStreamAsync(LlmRequest request, CancellationToken cancellationToken);
}
