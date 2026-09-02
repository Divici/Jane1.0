namespace Jane.Core.Vocabulary;

/// <summary>Where the strongest hints came from. Shown in the settings "last capture" panel.</summary>
public enum ContextSource
{
    None,

    /// <summary>Text the user had selected. The best signal available: it is what they are looking at.</summary>
    Selection,

    /// <summary>The paragraph around the caret.</summary>
    Surrounding,

    /// <summary>The focused control's value, for single-line fields with no text pattern.</summary>
    ControlValue,

    Mixed,
}

/// <summary>What happened on one Deep Context read. Logged for every dictation.</summary>
public enum ContextOutcome
{
    /// <summary>Deep Context was switched off in settings.</summary>
    Disabled,

    /// <summary>No read was started -- no focused window, or the pipeline never asked.</summary>
    NotAttempted,

    /// <summary>A read happened and produced terms.</summary>
    Read,

    /// <summary>A read happened and found nothing worth biasing on.</summary>
    Empty,

    /// <summary>The blocklist refused before any UI Automation call was made.</summary>
    Blocked,

    /// <summary>The focused control reports <c>IsPassword</c>. Never read, whatever else it offers.</summary>
    PasswordControl,

    /// <summary>Content came back, and the post-read blocklist threw all of it away.</summary>
    BlockedContent,

    /// <summary>The read did not finish inside the orchestrator's deadline. The pipeline moved on.</summary>
    TimedOut,

    /// <summary>The worker was still stuck on a previous read. That process is now blocklisted.</summary>
    WorkerWedged,

    /// <summary>
    /// A previous read was still running, but not long enough to count as stuck. Back-to-back
    /// dictation is legitimate, so this costs one press its context and nothing more.
    /// </summary>
    WorkerBusy,

    /// <summary>UI Automation itself is unavailable in this session.</summary>
    ProviderUnavailable,

    /// <summary>The focused element belonged to a different process than the dictation target.</summary>
    FocusMoved,

    /// <summary>The focused control exposes no text and no value.</summary>
    NoTextProvider,
}

/// <param name="MaxTerms">
/// How many terms leave this class. Capped because both consumers charge for length: sherpa-onnx
/// contextual biasing spreads a fixed budget of probability mass over the hotword list, so a
/// screen dump would dilute every real term toward nothing, and the LLM prompt is budgeted at
/// roughly 600 tokens in total. Two dozen terms is about fifty tokens and still leaves each term
/// enough weight to change a decoding.
/// </param>
/// <param name="MaxCharsScanned">
/// A hard ceiling on how much text is tokenised, so a read that came back with a whole document
/// cannot turn into a long CPU burst on the pipeline's critical path.
/// </param>
public sealed record ContextExtractionOptions
{
    public static ContextExtractionOptions Default { get; } = new();

    public int MaxTerms { get; init; } = 24;

    public int MinTermLength { get; init; } = 3;

    public int MaxTermLength { get; init; } = 40;

    public int MaxCharsScanned { get; init; } = 8_000;
}

/// <summary>
/// The small, ranked vocabulary Deep Context extracted from the focused window.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a copy of the screen. It feeds two consumers that both punish bulk:
/// sherpa-onnx contextual biasing, which is where the accuracy actually comes from, and the LLM
/// prompt. Phase 1's bench measured beam search with the bench's hotword list at 88 % of greedy's
/// p50 -- that headroom is what pays for biasing, and it exists only because the list is short.
/// </para>
/// <para>
/// The text fields are kept alongside the terms so the settings screen can show exactly what was
/// captured for the last dictation, which is a promise the plan makes to the user.
/// </para>
/// </remarks>
public sealed record ContextHints(
    IReadOnlyList<string> Terms,
    string? Selection,
    string? Surrounding,
    ContextSource Source)
{
    public static ContextHints Empty { get; } = new([], null, null, ContextSource.None);

    public bool IsEmpty => Terms.Count == 0;

    /// <summary>
    /// The list handed to <c>RecognitionOptions.Hotwords</c>.
    /// </summary>
    /// <remarks>
    /// Same list as <see cref="Terms"/>, named for its destination so the call site reads as the
    /// contract it is. Phase 10's dictionary entries are concatenated with these by the caller.
    /// </remarks>
    public IReadOnlyList<string> Hotwords => Terms;

    /// <summary>One line for the LLM prompt, or empty when there is nothing worth saying.</summary>
    /// <remarks>
    /// Just the terms. The surrounding prose is deliberately not pasted into the prompt: it would
    /// dominate a 600-token budget, and a language model handed a paragraph plus a transcript
    /// tends to answer the paragraph instead of formatting the transcript.
    /// </remarks>
    public string ToPromptFragment() =>
        IsEmpty ? string.Empty : $"Words visible in the user's current window: {string.Join(", ", Terms)}.";
}

/// <summary>
/// Turns whatever a window gave back into a short, ranked list of terms worth biasing toward.
/// </summary>
/// <remarks>
/// <para>
/// Three classes earn a place, in this order of usefulness to a speech recogniser:
/// <b>code identifiers</b> (<c>UiaWorker</c>, <c>kube_proxy</c>, <c>System.Text</c>), which no
/// general model has ever seen spelled that way; <b>proper nouns</b> (<c>Kubernetes</c>,
/// <c>Kate Chen</c>), which is the class the plan's acceptance test measures; and <b>jargon</b>,
/// meaning ordinary-looking words that are not common English.
/// </para>
/// <para>
/// Everything else is dropped, including anything shaped like a secret. That filter lives here as
/// well as in <see cref="Blocklist"/> on purpose: the blocklist decides whether a <em>read</em> is
/// allowed at all, and this decides what survives one that was. A hash or an access token is
/// useless as a hotword regardless, so dropping it costs nothing and removes a way for one to
/// reach a prompt.
/// </para>
/// <para>
/// Ranking is fully deterministic -- score, then first appearance -- so the same screen always
/// produces the same hotword list, which is what makes the eval corpus reproducible.
/// </para>
/// </remarks>
public static class ContextTermExtractor
{
    private const double SelectionWeight = 3.0;
    private const double ControlValueWeight = 2.0;
    private const double SurroundingWeight = 1.0;

    private const double CodeIdentifierScore = 5.0;
    private const double ProperNounScore = 4.0;
    private const double HyphenatedScore = 3.5;
    private const double JargonScore = 2.0;

    /// <summary>
    /// A capitalised word that only ever opens a sentence. Its capital is grammar rather than
    /// evidence of a name, so it ranks below a mid-sentence proper noun but above plain jargon --
    /// "Kubernetes." at the start of a line is still worth biasing toward.
    /// </summary>
    private const double SentenceInitialScore = 2.5;

    /// <summary>Extra credit per repeat, capped so one repeated word cannot crowd the list out.</summary>
    private const double RepeatBonus = 0.5;
    private const double MaxRepeatBonus = 2.0;

    /// <param name="selection">Text the user had selected, if any.</param>
    /// <param name="surrounding">The paragraph around the caret.</param>
    /// <param name="controlValue">The focused control's value, for fields with no text pattern.</param>
    public static ContextHints Extract(
        string? selection,
        string? surrounding,
        string? controlValue,
        ContextExtractionOptions? options = null)
    {
        options ??= ContextExtractionOptions.Default;

        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var order = 0;

        Harvest(selection, SelectionWeight, candidates, options, ref order);
        Harvest(controlValue, ControlValueWeight, candidates, options, ref order);
        Harvest(surrounding, SurroundingWeight, candidates, options, ref order);

        if (candidates.Count == 0)
        {
            return ContextHints.Empty with { Selection = Blank(selection), Surrounding = Blank(surrounding) };
        }

        var terms = candidates.Values
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Order)
            .Take(options.MaxTerms)
            .Select(static candidate => candidate.Text)
            .ToArray();

        return new ContextHints(terms, Blank(selection), Blank(surrounding), SourceOf(selection, surrounding, controlValue));

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Just the terms, for callers that already hold the text. Used by the eval corpus.</summary>
    public static IReadOnlyList<string> ExtractTerms(string? text, ContextExtractionOptions? options = null) =>
        Extract(text, null, null, options).Terms;

    private static ContextSource SourceOf(string? selection, string? surrounding, string? value)
    {
        var hasSelection = !string.IsNullOrWhiteSpace(selection);
        var hasSurrounding = !string.IsNullOrWhiteSpace(surrounding);
        var hasValue = !string.IsNullOrWhiteSpace(value);

        return (hasSelection, hasSurrounding, hasValue) switch
        {
            (true, false, false) => ContextSource.Selection,
            (false, true, false) => ContextSource.Surrounding,
            (false, false, true) => ContextSource.ControlValue,
            (false, false, false) => ContextSource.None,
            _ => ContextSource.Mixed,
        };
    }

    private static void Harvest(
        string? text,
        double weight,
        Dictionary<string, Candidate> candidates,
        ContextExtractionOptions options,
        ref int order)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var span = text.AsSpan(0, Math.Min(text.Length, options.MaxCharsScanned));
        var sentenceStart = true;

        for (var i = 0; i < span.Length;)
        {
            if (!IsTokenStart(span[i]))
            {
                // A terminator means the next word is sentence-initial, so its capital letter is
                // grammar rather than evidence that it is a name. A colon is deliberately not one:
                // "Owner: Kate" introduces a name far more often than it starts a sentence.
                if (span[i] is '.' or '!' or '?' or '\n' or '\r')
                {
                    sentenceStart = true;
                }

                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && IsTokenBody(span, i))
            {
                i++;
            }

            var token = span[start..i].Trim(TrimCharacters);
            var isInitial = sentenceStart;
            sentenceStart = false;

            if (token.Length < options.MinTermLength || token.Length > options.MaxTermLength)
            {
                continue;
            }

            var word = token.ToString();
            var score = ScoreOf(word, isInitial);
            if (score <= 0)
            {
                continue;
            }

            var key = word;
            if (candidates.TryGetValue(key, out var existing))
            {
                candidates[key] = existing with
                {
                    Score = existing.Score + Math.Min(RepeatBonus * weight, MaxRepeatBonus),
                };
            }
            else
            {
                candidates[key] = new Candidate(word, score * weight, order++);
            }
        }
    }

    private static readonly char[] TrimCharacters = ['.', '-', '_', '\'', '/', '’'];

    private static bool IsTokenStart(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// Inner punctuation is part of the token only when it joins two alphanumerics, which is what
    /// keeps <c>System.Text</c> and <c>kube-proxy</c> whole while dropping a full stop.
    /// </summary>
    private static bool IsTokenBody(ReadOnlySpan<char> span, int index)
    {
        var c = span[index];
        if (char.IsLetterOrDigit(c))
        {
            return true;
        }

        if (c is not ('.' or '-' or '_' or '\'' or '’' or '/'))
        {
            return false;
        }

        return index + 1 < span.Length && char.IsLetterOrDigit(span[index + 1]);
    }

    private static double ScoreOf(string word, bool sentenceInitial)
    {
        if (!word.Any(char.IsLetter))
        {
            return 0;
        }

        if (LooksLikeSecretOrNoise(word))
        {
            return 0;
        }

        if (IsCodeIdentifier(word))
        {
            return CodeIdentifierScore;
        }

        if (word.Contains('-', StringComparison.Ordinal) && word.Any(char.IsLower))
        {
            return HyphenatedScore;
        }

        var head = word[0];
        if (char.IsUpper(head) && word.Skip(1).Any(char.IsLower))
        {
            if (!sentenceInitial)
            {
                return ProperNounScore;
            }

            // "The" opening a sentence is grammar and nothing else; "Kubernetes" opening one is
            // still a term worth having, just with less evidence behind it.
            return CommonWords.Contains(word) ? 0 : SentenceInitialScore;
        }

        if (CommonWords.Contains(word))
        {
            return 0;
        }

        return word.Length >= 6 ? JargonScore : 0;
    }

    /// <summary>
    /// camelCase, PascalCase with an inner capital, snake_case, dotted paths, ALLCAPS acronyms and
    /// letter-digit mixes. These are the terms a general speech model has never seen spelled this
    /// way, so they are worth the most.
    /// </summary>
    private static bool IsCodeIdentifier(string word)
    {
        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var innerCapital = false;

        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (char.IsLower(c))
            {
                hasLower = true;
            }
            else if (char.IsUpper(c))
            {
                hasUpper = true;
                if (i > 0 && char.IsLower(word[i - 1]))
                {
                    innerCapital = true;
                }
            }
            else if (char.IsDigit(c))
            {
                hasDigit = true;
            }
        }

        if (innerCapital)
        {
            return true;
        }

        // Length five is what separates "System.Text" and "kube_proxy" from "e.g" and "a_b".
        if (word.Length >= 5 &&
            (word.Contains('_', StringComparison.Ordinal) || word.Contains('.', StringComparison.Ordinal)))
        {
            return true;
        }

        if (hasUpper && !hasLower && word.Count(char.IsUpper) >= 2)
        {
            return true;
        }

        return hasDigit && (hasLower || hasUpper) && word.Length >= 4;
    }

    /// <summary>
    /// Tokens that are useless as hotwords and dangerous in a prompt: hashes, opaque identifiers,
    /// keys, emails and hosts.
    /// </summary>
    private static bool LooksLikeSecretOrNoise(string word)
    {
        if (word.Contains('@', StringComparison.Ordinal) || word.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        if (word.Length >= 16 && !word.Contains('.', StringComparison.Ordinal) && !word.Contains('_', StringComparison.Ordinal))
        {
            var digits = word.Count(char.IsDigit);
            if (digits >= 4 && digits < word.Length)
            {
                // Long, unpunctuated, and part digits: a hash, a session id or a key. Never a word.
                return true;
            }
        }

        if (word.Length >= 24 && word.All(static c => char.IsAsciiHexDigit(c)))
        {
            return true;
        }

        return word.Length >= 12 && word.All(char.IsDigit);
    }

    private readonly record struct Candidate(string Text, double Score, int Order);

    /// <summary>
    /// Common English, used only to reject. It is short on purpose: a long frequency list would
    /// start rejecting real jargon, and the cost of letting one ordinary word through is a wasted
    /// hotword slot, not a wrong transcription.
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "her", "was", "one",
        "our", "out", "day", "get", "has", "him", "his", "how", "its", "new", "now", "old", "see",
        "two", "way", "who", "did", "she", "use", "man", "men", "put", "say", "too",
        "with", "this", "that", "from", "they", "have", "been", "will", "would", "there", "their",
        "what", "about", "which", "when", "make", "like", "time", "just", "know", "take", "into",
        "your", "some", "could", "them", "other", "than", "then", "look", "only", "come", "over",
        "think", "also", "back", "after", "work", "first", "well", "even", "want", "because",
        "these", "give", "most", "here", "should", "very", "still", "being", "does", "made",
        "before", "must", "such", "through", "where", "much", "those", "same", "while", "might",
        "shall", "under", "again", "each", "between", "both", "against", "during", "without",
        "another", "around", "however", "something", "anything", "everything", "nothing",
        "someone", "anyone", "everyone", "please", "thanks", "thank", "hello", "going", "really",
        "actually", "probably", "maybe", "little", "always", "never", "every", "since", "until",
        "already", "enough", "though", "quite", "rather", "perhaps", "instead", "together",
        "sorry", "right", "wrong", "better", "worse", "great", "small", "large", "long", "short",
        "next", "last", "part", "line", "text", "file", "name", "list", "case", "point", "place",
        "thing", "things", "people", "number", "value", "change", "changes", "using", "used",
        "need", "needs", "needed", "help", "sure", "yeah", "okay", "wouldn", "didn",
        "doesn", "isn", "aren", "won", "cannot", "let", "lets", "got", "goes", "went",
        "said", "says", "tell", "told", "call", "called", "keep", "kept", "find", "found",
        "start", "started", "stop", "stopped", "open", "close", "closed", "read", "write",
        "written", "send", "sent", "down", "away",
    };
}
