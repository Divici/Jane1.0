using System.Diagnostics;
using System.Text;
using Jane.Core.Abstractions;

namespace Jane.Core.Formatting;

/// <param name="Model">
/// Server-side tag. Jane-owned, so a re-pull of the upstream tag cannot drift it. Ignored by
/// Phase 6's route-aware client, which picks the model and device from the governor's decision;
/// it is what a client talking straight to Ollama uses, which is the eval harness and the tests.
/// </param>
/// <param name="Timeout">
/// The hard deadline on the whole call, model load included. This is the only deadline on the
/// formatting stage -- the transport's own HTTP timeout is two minutes, which is not a deadline
/// a person would wait out.
/// <para>
/// Sized for the warm path, with the cold path deliberately allowed to miss. Measured: warm
/// generation for a one-sentence transcript is 50-200 ms, and a long one is a second or two; a
/// cold load is around twenty seconds. Five seconds therefore clears every warm case several
/// times over and gives up quickly on a cold one.
/// </para>
/// <para>
/// This was twenty seconds, sized for the cold load on the reasoning that key-down warm-up would
/// "mostly hide it behind the speech" and that losing filler removal once per boot would be more
/// visible than one long wait. The field says otherwise: a 4.6-second dictation took 20.2 seconds
/// end to end and the user watched the pill say "formatting" for all of it. A wait that long is
/// not hidden by anything.
/// </para>
/// <para>
/// Giving up early costs less than it appears to, because the warm-up started at key-down runs on
/// its own task with no cancellation token and survives this deadline. So the cold dictation
/// falls back to raw text after five seconds and the model finishes loading anyway, which leaves
/// the next dictation warm. On expiry the raw ASR text is injected -- never nothing, and Parakeet
/// already emits punctuation and casing.
/// </para>
/// </param>
/// <param name="MaxOutputTokens">
/// Ceiling on <c>num_predict</c>. The per-request cap is derived from the transcript on top of
/// this, so a four-word dictation cannot fund a thousand-token continuation.
/// </param>
public sealed record TranscriptFormatterOptions
{
    public string Model { get; init; } = "jane-qwen3-4b";

    public LlmDevice Device { get; init; } = LlmDevice.Gpu;

    public int NumCtx { get; init; } = 8192;

    public string KeepAlive { get; init; } = "180s";

    public double Temperature { get; init; } = 0.2;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxOutputTokens { get; init; } = 512;

    public BypassOptions Bypass { get; init; } = new();

    public OutputValidatorOptions Validation { get; init; } = new();

    public PromptOptions Prompt { get; init; } = new();
}

/// <summary>
/// Turns a raw transcript into what the user meant to write -- and knows when not to.
/// </summary>
/// <remarks>
/// <para>
/// Three outcomes, one code path. The LLM is skipped because the transcript needs nothing
/// (<see cref="FormattingRoute.Bypassed"/>), run and trusted
/// (<see cref="FormattingRoute.Formatted"/>), or run and overruled
/// (<see cref="FormattingRoute.RawFallback"/>). All three end the same way -- text goes to the
/// injector and a <see cref="FormattingOutcome"/> goes to the log -- which is what makes the
/// bypass measurable rather than merely believed.
/// </para>
/// <para>
/// The fourth possibility, skipping because a game is running, is deliberately not here. Phase 6
/// applies it one layer out in <c>RoutedFormatter</c>, so this class never learns what a GPU is
/// and the resource policy stays in one place.
/// </para>
/// <para>
/// The class never throws for a model or transport failure. Every failure mode ends with the raw
/// ASR text, because Parakeet's output is already punctuated and cased and is therefore always a
/// usable answer; injecting nothing would lose the user's words. The one exception is the
/// caller's own cancellation, which propagates so the orchestrator can reach
/// <c>Cancelled</c> rather than silently typing text the user pressed Escape to abandon.
/// </para>
/// </remarks>
public sealed class TranscriptFormatter : ITranscriptFormatter
{
    private readonly ILlmClient _llm;
    private readonly TranscriptFormatterOptions _options;
    private readonly IFormattingPolicySource _policies;
    private readonly IFormattingLog _log;
    private readonly PromptBuilder _prompts;
    private readonly BypassHeuristic _bypass;
    private readonly OutputValidator _validator;
    private readonly TimeProvider _time;

    public TranscriptFormatter(
        ILlmClient llm,
        TranscriptFormatterOptions? options = null,
        IFormattingPolicySource? policies = null,
        IFormattingLog? log = null,
        TimeProvider? timeProvider = null)
    {
        _llm = llm;
        _options = options ?? new TranscriptFormatterOptions();
        _policies = policies ?? EmptyFormattingPolicySource.Instance;
        _log = log ?? NullFormattingLog.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _prompts = new PromptBuilder(_options.Prompt);
        _bypass = new BypassHeuristic(_options.Bypass);
        _validator = new OutputValidator(_options.Validation);
    }

    public async Task<string> FormatAsync(
        string transcript, FormattingContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var policy = _policies.Resolve(transcript, context);
        var decision = _bypass.Evaluate(transcript, context, policy);

        if (decision.Bypassed)
        {
            return Finish(transcript, transcript, FormattingRoute.Bypassed, decision,
                FormattingFallback.None, decision.Reason, stopwatch, context, 0, 0);
        }

        var request = new LlmRequest(
            Model: _options.Model,
            SystemPrompt: PromptBuilder.SystemPrompt,
            UserPrompt: _prompts.BuildUserPrompt(transcript, context, policy),
            Device: _options.Device,
            NumCtx: _options.NumCtx,
            KeepAlive: _options.KeepAlive,
            Think: false,
            Temperature: _options.Temperature,
            MaxTokens: OutputTokenCap(transcript));

        // Linked rather than replacing: the caller's token still cancels, and the deadline is
        // ours alone, so a timeout and an Escape press stay distinguishable below.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        Generation generation;
        try
        {
            generation = await GenerateAsync(request, transcript, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Finish(transcript, transcript, FormattingRoute.RawFallback, decision,
                FormattingFallback.Timeout,
                $"No usable response within {_options.Timeout.TotalMilliseconds:F0} ms.",
                stopwatch, context, 0, 0);
        }
        catch (Exception ex)
        {
            return Finish(transcript, transcript, FormattingRoute.RawFallback, decision,
                FormattingFallback.TransportError, ex.Message, stopwatch, context, 0, 0);
        }

        var verdict = _validator.Validate(transcript, generation.Text);

        return verdict.Accepted
            ? Finish(transcript, verdict.Text, FormattingRoute.Formatted, decision,
                FormattingFallback.None, null, stopwatch, context,
                generation.PromptTokens, generation.CompletionTokens)
            : Finish(transcript, transcript, FormattingRoute.RawFallback, decision,
                verdict.Reason, verdict.Detail, stopwatch, context,
                generation.PromptTokens, generation.CompletionTokens);
    }

    /// <summary>
    /// Scales <c>num_predict</c> to the transcript.
    /// </summary>
    /// <remarks>
    /// Formatting is close to length-preserving, so three tokens per spoken word plus a flat
    /// allowance covers punctuation, list markers and a dictionary substitution or two. It is a
    /// second, cheaper defence against a runaway continuation: the server stops generating rather
    /// than Jane stopping reading.
    /// </remarks>
    private int OutputTokenCap(string transcript) =>
        Math.Clamp((FormattingLexicon.CountWords(transcript) * 3) + 48, 64, _options.MaxOutputTokens);

    private async Task<Generation> GenerateAsync(
        LlmRequest request, string transcript, CancellationToken cancellationToken)
    {
        if (_llm is not IStreamingLlmClient streaming)
        {
            var response = await _llm.ChatAsync(request, cancellationToken);
            return new Generation(response.Text, response.PromptTokens, response.CompletionTokens);
        }

        var ceiling = _validator.MaxAcceptedLength(transcript);
        var builder = new StringBuilder();

        await foreach (var chunk in streaming.ChatStreamAsync(request, cancellationToken))
        {
            builder.Append(chunk);

            // Past the ceiling the answer is going to be rejected whatever else arrives. Reading
            // the rest of it would spend the timeout budget on text nobody will see.
            if (builder.Length > ceiling)
            {
                break;
            }
        }

        // Zero token counts: the stream yields text, and the counts only arrive in the transport's
        // final frame. The outcome loses them on this path, which costs the eval a diagnostic and
        // nothing else -- latency is measured with a stopwatch either way.
        return new Generation(builder.ToString(), 0, 0);
    }

    private string Finish(
        string transcript,
        string finalText,
        FormattingRoute route,
        BypassDecision decision,
        FormattingFallback fallback,
        string? detail,
        Stopwatch stopwatch,
        FormattingContext context,
        int promptTokens,
        int completionTokens)
    {
        _log.Record(new FormattingOutcome(
            At: _time.GetUtcNow(),
            ProcessName: context.TargetProcessName,
            RawTranscript: transcript,
            FinalText: finalText,
            Route: route,
            Bypass: decision,
            Fallback: fallback,
            Detail: detail,
            Model: _options.Model,
            Device: _options.Device,
            Elapsed: stopwatch.Elapsed,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens));

        return finalText;
    }

    private readonly record struct Generation(string Text, int PromptTokens, int CompletionTokens);
}
