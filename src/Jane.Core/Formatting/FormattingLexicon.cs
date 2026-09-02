using System.Text;

namespace Jane.Core.Formatting;

/// <summary>
/// The filler and self-correction markers whose presence means the LLM has work to do.
/// </summary>
/// <remarks>
/// <para>
/// The list is deliberately over-inclusive. "like", "actually" and "sorry" are ordinary words as
/// often as they are disfluencies, so they will block the bypass on transcripts that did not need
/// the LLM. That trade is the whole point of BLOCKER #9: a wrong block costs one LLM call
/// (~400 ms); a wrong bypass silently discards a spoken self-correction and types the sentence
/// the user explicitly retracted. The costs are not symmetric, so neither is the list.
/// </para>
/// <para>
/// Matching is whole-word and multi-word aware -- "no wait" is invisible to a token scan, and
/// "um" inside "album" must not cost every dictation the LLM.
/// </para>
/// </remarks>
public static class FormattingLexicon
{
    /// <summary>Sounds and phrases that carry no meaning and should come out of the text.</summary>
    public static IReadOnlyList<string> Fillers { get; } =
    [
        "um", "umm", "uhm", "uh", "uhh", "er", "erm", "ah", "ahh", "hmm",
        "like", "you know", "y'know", "sort of", "kind of", "kinda", "sorta",
        "basically", "literally", "i guess", "i suppose",
    ];

    /// <summary>
    /// Phrases that retract what was just said. Missing one of these is the expensive failure:
    /// the abandoned words get typed and the correction does not.
    /// </summary>
    public static IReadOnlyList<string> SelfCorrections { get; } =
    [
        "no wait", "wait no", "scratch that", "strike that", "i mean", "i meant",
        "make that", "actually", "sorry", "rather", "let me rephrase", "or rather",
        "no sorry", "correction",
    ];

    /// <summary>Both lists, longest first so "no wait" is reported rather than nothing.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. Fillers.Concat(SelfCorrections).OrderByDescending(m => m.Length).ThenBy(m => m, StringComparer.Ordinal)];

    /// <summary>Every marker present in the transcript, longest first.</summary>
    public static IReadOnlyList<string> FindHits(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return [];
        }

        // Padded on both sides so a marker at either end still matches with space delimiters,
        // which is what makes the whole-word rule a single Contains rather than a regex per marker.
        var padded = $" {Normalise(transcript)} ";

        return [.. All.Where(marker => padded.Contains($" {marker} ", StringComparison.Ordinal))];
    }

    /// <summary>
    /// Whether a term occurs in the transcript as a whole word, under the same normalisation the
    /// lexicon uses.
    /// </summary>
    /// <remarks>
    /// Normalising both sides is what lets a dictionary entry like <c>SettingsStore</c> or
    /// <c>Jane.Core</c> match speech transcribed as "settings store" and "jane.core" -- the user
    /// writes the term the way they want it written, not the way ASR happens to render it.
    /// </remarks>
    public static bool ContainsWholeWord(string transcript, string term)
    {
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var haystack = $" {Normalise(transcript)} ";
        var needle = Normalise(term);

        if (needle.Length > 0 && haystack.Contains($" {needle} ", StringComparison.Ordinal))
        {
            return true;
        }

        // Also match the term as it would be *spoken*. A dictionary entry is written the way the
        // user wants it typed -- "SettingsStore", "getUserById" -- but ASR renders it as separate
        // words, so the written form alone would never match a code identifier, which is exactly
        // the case the dictionary is most useful for.
        var spoken = SplitCamelCase(term);
        return spoken.Length > 0 && spoken != needle && haystack.Contains($" {spoken} ", StringComparison.Ordinal);
    }

    /// <summary>Turns <c>SettingsStore</c> into <c>settings store</c>, the way it is dictated.</summary>
    private static string SplitCamelCase(string term)
    {
        var builder = new System.Text.StringBuilder(term.Length + 8);

        for (var i = 0; i < term.Length; i++)
        {
            // A capital that follows a lower-case letter or digit opens a new spoken word.
            // Runs of capitals stay together, so "CSV" does not become "c s v".
            if (i > 0 && char.IsUpper(term[i]) && !char.IsUpper(term[i - 1]) && char.IsLetterOrDigit(term[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(term[i]);
        }

        return Normalise(builder.ToString());
    }

    /// <summary>Lower-cases, drops punctuation and collapses runs of whitespace to one space.</summary>
    /// <remarks>
    /// Apostrophes survive so "y'know" and "don't" stay one word; everything else becomes a
    /// separator, which is what makes "no-wait" and "no, wait" match the same marker.
    /// </remarks>
    public static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character is '\'' or '’')
            {
                builder.Append(char.ToLowerInvariant(character == '’' ? '\'' : character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Word count on the normalised form, so punctuation never inflates it.</summary>
    public static int CountWords(string text)
    {
        var normalised = Normalise(text);
        if (normalised.Length == 0)
        {
            return 0;
        }

        var words = 1;
        foreach (var character in normalised)
        {
            if (character == ' ')
            {
                words++;
            }
        }

        return words;
    }
}
