using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jane.Core.Formatting;

/// <summary>Why a dictation fell back to raw ASR text instead of the model's output.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FormattingFallback>))]
public enum FormattingFallback
{
    /// <summary>No fallback: the model's output was used.</summary>
    None,

    /// <summary>Nothing came back, or nothing survived cleaning.</summary>
    Empty,

    /// <summary>Far longer than the transcript -- a continuation, an explanation or a poem.</summary>
    TooLong,

    /// <summary>Far shorter than the transcript -- a summary or a one-word answer.</summary>
    TooShort,

    /// <summary>A dictated question came back as a statement, so it was answered, not formatted.</summary>
    AnsweredQuestion,

    /// <summary>The model talked about the task instead of doing it.</summary>
    Commentary,

    /// <summary>The hard deadline expired. The raw text goes in rather than nothing.</summary>
    Timeout,

    /// <summary>The server was unreachable or errored.</summary>
    TransportError,
}

/// <param name="MaxGrowthFactor">
/// Formatting adds punctuation, list markers and line breaks; it does not add paragraphs. Anything
/// past this multiple of the transcript is a continuation, not a cleanup.
/// </param>
/// <param name="GrowthSlackChars">
/// A flat allowance so a three-word dictation is not held to three words times a factor.
/// </param>
/// <param name="MinRetainedFraction">Below this share of the transcript, the model summarised.</param>
/// <param name="CollapseFloorChars">
/// Short transcripts are allowed to collapse freely -- filler dominates them, and "um so like send
/// it tuesday no wait wednesday" legitimately becomes three words. The shrink rule only applies
/// above this length.
/// </param>
public sealed record OutputValidatorOptions
{
    public double MaxGrowthFactor { get; init; } = 1.6;

    public int GrowthSlackChars { get; init; } = 48;

    public double MinRetainedFraction { get; init; } = 0.25;

    public int CollapseFloorChars { get; init; } = 60;
}

/// <param name="Text">The cleaned output when accepted; the empty string when not.</param>
/// <param name="Detail">What was observed, for the log. Never shown to the user.</param>
public sealed record OutputVerdict(bool Accepted, FormattingFallback Reason, string Detail, string Text)
{
    public static OutputVerdict Ok(string text) => new(true, FormattingFallback.None, "Accepted.", text);

    public static OutputVerdict Reject(FormattingFallback reason, string detail) =>
        new(false, reason, detail, string.Empty);
}

/// <summary>
/// Decides whether the model formatted the transcript or answered it.
/// </summary>
/// <remarks>
/// <para>
/// The prompt asks the model to treat the transcript as content. A 4B model will sometimes
/// oblige a dictated "write a poem" anyway, and there is no way to make a prompt a guarantee.
/// This is the guarantee: anything that reads as an answer, a refusal, a summary or a
/// continuation is discarded and the raw ASR text is injected instead. Raw Parakeet output is
/// already punctuated and cased, so falling back costs polish, not usability.
/// </para>
/// <para>
/// Cleaning and rejecting are different things and the split is deliberate. Code fences, leftover
/// tags and quotes the model wrapped round the whole answer are formatting artefacts with one
/// obvious repair, so they are stripped. Commentary, answers and summaries are the model doing
/// the wrong job; repairing those would hide the misbehaviour from the eval and ship whatever
/// was left.
/// </para>
/// </remarks>
public sealed partial class OutputValidator(OutputValidatorOptions? options = null)
{
    /// <summary>
    /// Phrases that only appear when the model is talking about the task.
    /// </summary>
    /// <remarks>
    /// Each is checked against the transcript too: a person really can dictate "here is the
    /// cleaned text", and when they do it is content, not commentary.
    /// </remarks>
    private static readonly string[] MetaPhrases =
    [
        "here is the cleaned", "here's the cleaned", "here is the formatted", "here's the formatted",
        "here is the corrected", "here's the corrected", "here is the text", "here's the text",
        "cleaned text", "formatted text", "corrected version", "the cleaned version",
        "as an ai", "i cannot help", "i cannot assist", "i can't help", "i can't assist",
        "i'm unable to", "i am unable to", "let me know if", "hope this helps",
    ];

    /// <summary>
    /// Openers that mark the first clause as a question.
    /// </summary>
    /// <remarks>
    /// Multi-word forms carry their weight; bare "do" and "is" would fire on "do the thing" and
    /// cost a fallback for nothing.
    /// </remarks>
    private static readonly string[] QuestionOpeners =
    [
        "what", "whats", "when", "where", "why", "how", "who", "whom", "whose", "which",
        "can you", "can we", "can i", "could you", "could we", "would you", "will you",
        "do you", "did you", "does it", "does this", "are you", "are we", "is it", "is this",
        "is there", "are there", "should i", "should we", "shall we", "have you", "any chance",
    ];

    /// <summary>Words that precede the real start of a sentence and say nothing about its shape.</summary>
    private static readonly HashSet<string> LeadingNoise = new(StringComparer.Ordinal)
    {
        "hey", "hi", "hello", "um", "umm", "uh", "uhh", "er", "erm", "ah", "so", "ok", "okay",
        "well", "and", "but", "like", "yeah", "yep", "alright", "right", "please", "just", "now",
    };

    private readonly OutputValidatorOptions _options = options ?? new OutputValidatorOptions();

    public OutputValidatorOptions Options => _options;

    /// <summary>
    /// The longest output that could still be a formatting of this transcript.
    /// </summary>
    /// <remarks>
    /// Exposed so a streaming caller can stop pulling tokens the moment a runaway generation
    /// passes the point of no return, rather than waiting for the whole poem to arrive and then
    /// discarding it.
    /// </remarks>
    public int MaxAcceptedLength(string transcript) =>
        (int)(transcript.Length * _options.MaxGrowthFactor) + _options.GrowthSlackChars;

    public OutputVerdict Validate(string transcript, string candidate)
    {
        var text = Clean(candidate, transcript);

        if (text.Length == 0)
        {
            return OutputVerdict.Reject(FormattingFallback.Empty, "Model returned nothing usable.");
        }

        var normalisedCandidate = FormattingLexicon.Normalise(text);
        var normalisedTranscript = FormattingLexicon.Normalise(transcript);

        foreach (var phrase in MetaPhrases)
        {
            if (normalisedCandidate.Contains(phrase, StringComparison.Ordinal) &&
                !normalisedTranscript.Contains(phrase, StringComparison.Ordinal))
            {
                return OutputVerdict.Reject(FormattingFallback.Commentary, $"Output contains \"{phrase}\".");
            }
        }

        var ceiling = MaxAcceptedLength(transcript);
        if (text.Length > ceiling)
        {
            return OutputVerdict.Reject(
                FormattingFallback.TooLong, $"{text.Length} chars against a {ceiling}-char ceiling.");
        }

        if (transcript.Length > _options.CollapseFloorChars)
        {
            var floor = (int)(transcript.Length * _options.MinRetainedFraction);
            if (text.Length < floor)
            {
                return OutputVerdict.Reject(
                    FormattingFallback.TooShort, $"{text.Length} chars against a {floor}-char floor.");
            }
        }

        // A question that went in and did not come back out is the signature of a model that
        // answered the transcript. "?" anywhere is enough -- requiring it at the end would reject
        // a correctly formatted "Can you send it tomorrow? That would be great."
        if (LooksLikeAQuestion(normalisedTranscript) && !text.Contains('?', StringComparison.Ordinal))
        {
            return OutputVerdict.Reject(
                FormattingFallback.AnsweredQuestion, "A dictated question came back as a statement.");
        }

        return OutputVerdict.Ok(text);
    }

    /// <summary>Strips formatting artefacts that have exactly one sensible repair.</summary>
    public static string Clean(string candidate, string transcript)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var text = CodeFence().Replace(candidate, string.Empty);
        text = PromptBuilder.Sanitise(text).Trim();

        // Wrapping quotes the model added round the whole answer. Left alone when the speaker
        // dictated quotes themselves, either as characters or as spoken punctuation.
        if (text.Length > 1 &&
            text[0] == '"' &&
            text[^1] == '"' &&
            text.Count(c => c == '"') == 2 &&
            !transcript.Contains('"', StringComparison.Ordinal) &&
            !MentionsQuoting(transcript))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private static bool MentionsQuoting(string transcript)
    {
        var words = FormattingLexicon.Normalise(transcript).Split(' ');
        return words.Contains("quote") || words.Contains("quotes") || words.Contains("unquote");
    }

    private static bool LooksLikeAQuestion(string normalisedTranscript)
    {
        if (normalisedTranscript.Length == 0)
        {
            return false;
        }

        var words = normalisedTranscript.Split(' ');
        var start = 0;
        while (start < words.Length && LeadingNoise.Contains(words[start]))
        {
            start++;
        }

        if (start >= words.Length)
        {
            return false;
        }

        var opening = string.Join(' ', words.Skip(start).Take(2));
        return QuestionOpeners.Contains(words[start], StringComparer.Ordinal) ||
               QuestionOpeners.Contains(opening, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\s*```[a-zA-Z]*\s*|\s*```\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFence();
}
