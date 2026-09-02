using System.Globalization;
using System.Text;

namespace Jane.Core.Text;

/// <param name="Substitutions">Wrong word in the right place -- the error class users notice most.</param>
public sealed record ErrorCounts(int Substitutions, int Deletions, int Insertions, int ReferenceWords)
{
    public int Total => Substitutions + Deletions + Insertions;

    /// <summary>Errors per reference word. 0 is perfect; values above 1 are possible with runaway insertions.</summary>
    public double Rate => ReferenceWords == 0 ? (Total == 0 ? 0 : 1) : Total / (double)ReferenceWords;
}

/// <summary>
/// Word error rate, and the punctuation and casing accuracy the plan scores separately.
/// </summary>
/// <remarks>
/// WER is computed over normalised text -- lower-cased, punctuation stripped -- which is the
/// Open ASR Leaderboard convention and what makes numbers comparable to published ones. But
/// normalising away punctuation and casing would discard exactly what makes Parakeet worth
/// choosing, so those are scored as their own metrics against the un-normalised reference.
/// </remarks>
public static class WordErrorRate
{
    public static ErrorCounts Compute(string reference, string hypothesis)
    {
        var referenceWords = Normalize(reference);
        var hypothesisWords = Normalize(hypothesis);
        return Levenshtein(referenceWords, hypothesisWords);
    }

    public static double Rate(string reference, string hypothesis) => Compute(reference, hypothesis).Rate;

    /// <summary>
    /// Fraction of reference punctuation marks the hypothesis also produced, by multiset overlap.
    /// </summary>
    /// <remarks>
    /// Position-free on purpose: a comma one word early is a far smaller error than a missing
    /// sentence boundary, and an alignment-based score would weight them the same.
    /// </remarks>
    public static double PunctuationAccuracy(string reference, string hypothesis)
    {
        var expected = CountMarks(reference);
        if (expected.Count == 0)
        {
            return CountMarks(hypothesis).Count == 0 ? 1 : 0;
        }

        var actual = CountMarks(hypothesis);
        var matched = expected.Sum(kv => Math.Min(kv.Value, actual.TryGetValue(kv.Key, out var n) ? n : 0));
        var total = expected.Values.Sum();

        // Spurious marks are penalised too: a model that emits a comma every three words would
        // otherwise score perfectly.
        var spurious = actual.Sum(kv => Math.Max(0, kv.Value - (expected.TryGetValue(kv.Key, out var n) ? n : 0)));
        return Math.Clamp((matched - spurious) / (double)total, 0, 1);
    }

    /// <summary>
    /// Fraction of aligned word pairs whose capitalisation agrees, over words that match
    /// case-insensitively. Words the recogniser got outright wrong are WER's problem, not this.
    /// </summary>
    public static double CasingAccuracy(string reference, string hypothesis)
    {
        var referenceWords = SplitWords(reference);
        var hypothesisWords = SplitWords(hypothesis);

        var comparable = 0;
        var agreeing = 0;
        var limit = Math.Min(referenceWords.Length, hypothesisWords.Length);

        for (var i = 0; i < limit; i++)
        {
            if (!string.Equals(referenceWords[i], hypothesisWords[i], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            comparable++;
            if (string.Equals(referenceWords[i], hypothesisWords[i], StringComparison.Ordinal))
            {
                agreeing++;
            }
        }

        return comparable == 0 ? 1 : agreeing / (double)comparable;
    }

    /// <summary>Whether every listed term appears in the hypothesis, case-insensitively.</summary>
    public static double TermRecall(string hypothesis, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return 1;
        }

        var found = terms.Count(term =>
            hypothesis.Contains(term, StringComparison.OrdinalIgnoreCase));
        return found / (double)terms.Count;
    }

    /// <summary>Lower-case, strip punctuation, collapse whitespace -- the leaderboard convention.</summary>
    public static string[] Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch == '\'')
            {
                // Keep the apostrophe: "don't" and "dont" are the same word, and splitting them
                // into two would invent an insertion on every contraction.
                builder.Append(ch);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] SplitWords(string text) =>
        text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '(', ')'))
            .Where(w => w.Length > 0)
            .ToArray();

    private static Dictionary<char, int> CountMarks(string text)
    {
        var counts = new Dictionary<char, int>();
        foreach (var ch in text)
        {
            if (".,!?;:".Contains(ch, StringComparison.Ordinal))
            {
                counts[ch] = counts.GetValueOrDefault(ch) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Standard edit distance with backtracking to separate substitutions, deletions and
    /// insertions -- the three are reported apart because they mean different things about an
    /// engine, and a single distance would hide, say, a model that drops the ends of sentences.
    /// </summary>
    private static ErrorCounts Levenshtein(string[] reference, string[] hypothesis)
    {
        var rows = reference.Length + 1;
        var columns = hypothesis.Length + 1;
        var cost = new int[rows, columns];

        for (var i = 0; i < rows; i++)
        {
            cost[i, 0] = i;
        }

        for (var j = 0; j < columns; j++)
        {
            cost[0, j] = j;
        }

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < columns; j++)
            {
                var match = string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal) ? 0 : 1;
                cost[i, j] = Math.Min(
                    Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1),
                    cost[i - 1, j - 1] + match);
            }
        }

        int substitutions = 0, deletions = 0, insertions = 0;
        var x = reference.Length;
        var y = hypothesis.Length;

        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0)
            {
                var match = string.Equals(reference[x - 1], hypothesis[y - 1], StringComparison.Ordinal) ? 0 : 1;
                if (cost[x, y] == cost[x - 1, y - 1] + match)
                {
                    if (match == 1)
                    {
                        substitutions++;
                    }

                    x--;
                    y--;
                    continue;
                }
            }

            if (x > 0 && cost[x, y] == cost[x - 1, y] + 1)
            {
                deletions++;
                x--;
                continue;
            }

            insertions++;
            y--;
        }

        return new ErrorCounts(substitutions, deletions, insertions, reference.Length);
    }

    public static string Describe(ErrorCounts counts) =>
        string.Create(CultureInfo.InvariantCulture,
            $"WER {counts.Rate:P1} ({counts.Substitutions}S {counts.Deletions}D {counts.Insertions}I over {counts.ReferenceWords} words)");
}
