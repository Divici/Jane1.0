using Jane.Core.Abstractions;
using Jane.Core.Modes;

namespace Jane.Core.Pipeline;

/// <param name="Selection">What was selected, if anything. Drives the mode.</param>
/// <param name="Hotwords">Terms for sherpa-onnx contextual biasing and the LLM prompt.</param>
/// <param name="ScreenContext">On-screen text, for spelling names correctly. Never answered.</param>
public sealed record DictationContext(
    SelectionResult Selection,
    IReadOnlyList<string> Hotwords,
    string? ScreenContext = null,
    string? ControlType = null)
{
    public static DictationContext Empty { get; } = new(SelectionResult.None, []);
}

/// <summary>
/// Reads whatever the focused window will tell Jane, starting at key-down.
/// </summary>
/// <remarks>
/// Optional throughout. Deep Context is a quality improvement, not a requirement -- if it is off,
/// blocklisted, or the provider is wedged, dictation proceeds without it and the only cost is that
/// a proper noun may be spelled the way it sounded.
/// <para>
/// Split into begin and collect because the two happen at different moments: the read is started
/// at key-down so it overlaps with the user speaking, and collected after key-up, by which time it
/// has usually finished and costs nothing.
/// </para>
/// </remarks>
public interface IDictationContextSource
{
    /// <summary>Starts a read. Must not block and must not throw.</summary>
    void BeginRead(TargetWindow target);

    /// <summary>Collects the read, giving up at the deadline and returning what is known.</summary>
    Task<DictationContext> CollectAsync(TargetWindow target, CancellationToken cancellationToken);
}

/// <summary>The default when Deep Context is switched off: nothing is read, nothing is delayed.</summary>
public sealed class NullContextSource : IDictationContextSource
{
    public static NullContextSource Instance { get; } = new();

    public void BeginRead(TargetWindow target)
    {
    }

    public Task<DictationContext> CollectAsync(TargetWindow target, CancellationToken cancellationToken) =>
        Task.FromResult(DictationContext.Empty);
}

/// <summary>Rewrites a selection according to a spoken instruction.</summary>
/// <remarks>
/// Separate from <see cref="ITranscriptFormatter"/> because the two prompts are genuinely
/// different jobs: one tidies what was just said, the other transforms text that already exists
/// according to what was just said.
/// </remarks>
public interface ISelectionRewriter
{
    Task<string> RewriteAsync(
        string selection, string instruction, FormattingContext context, CancellationToken cancellationToken);
}

/// <summary>Sends the Enter that "Send it" asks for, after injection has been verified.</summary>
public interface ISubmitter
{
    Task SubmitAsync(TargetWindow target, CancellationToken cancellationToken);
}

/// <summary>
/// Reports that a rewrite is impossible rather than quietly leaving the selection untouched.
/// </summary>
/// <remarks>
/// The default when no LLM is available -- formatting disabled, Ollama down, or a game running.
/// Edit Mode's local operations (delete, replace, undo) still work; only a model-driven rewrite
/// cannot, and the user is told so.
/// </remarks>
public sealed class UnavailableRewriter : ISelectionRewriter
{
    public static UnavailableRewriter Instance { get; } = new();

    public Task<string> RewriteAsync(
        string selection, string instruction, FormattingContext context, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);
}
