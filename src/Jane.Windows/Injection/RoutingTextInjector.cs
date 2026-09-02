using Jane.Core.Abstractions;

namespace Jane.Windows.Injection;

/// <summary>
/// The single <see cref="ITextInjector"/> the rest of Jane talks to: picks a strategy per target
/// app, delegates, and recovers when the clipboard turns out to be unavailable.
/// </summary>
/// <remarks>
/// <para>
/// Keeping the choice here rather than inside either strategy means each injector stays a
/// straight-line implementation of one mechanism, and the reason a strategy was chosen is
/// recorded on the result rather than being implicit in which type ran.
/// </para>
/// <para>
/// The one recovery path is a clipboard Jane could not read -- another process holding it, most
/// often a clipboard manager mid-capture. That is transient and losing a finished dictation to
/// it would be the wrong trade, so the text is typed instead. No other failure is retried: a
/// held modifier, a changed target and a UIPI refusal would all fail identically the second
/// time, and retrying a UIPI refusal would just type half a sentence somewhere twice.
/// </para>
/// </remarks>
public sealed class RoutingTextInjector(
    InjectionStrategySelector selector,
    SendInputInjector unicodeInjector,
    ClipboardInjector clipboardInjector) : ITextInjector
{
    public async Task<InjectionResult> InjectAsync(
        string text, TargetWindow target, CancellationToken cancellationToken)
    {
        var decision = selector.Select(target, text.Length);

        var result = decision.Strategy == InjectionStrategy.Clipboard
            ? await clipboardInjector.InjectAsync(text, target, cancellationToken).ConfigureAwait(false)
            : await unicodeInjector.InjectAsync(text, target, cancellationToken).ConfigureAwait(false);

        if (result.Failure == InjectionFailure.ClipboardUnavailable)
        {
            var fallback = await unicodeInjector.InjectAsync(text, target, cancellationToken).ConfigureAwait(false);
            return Annotate(fallback, $"Clipboard unavailable ({result.Detail}); typed the text instead.");
        }

        return Annotate(result, decision.Reason);
    }

    /// <summary>Carries the routing reason onto the result, for history and the eval corpus.</summary>
    private static InjectionResult Annotate(InjectionResult result, string reason) =>
        result.Detail is null ? result with { Detail = reason } : result;
}
