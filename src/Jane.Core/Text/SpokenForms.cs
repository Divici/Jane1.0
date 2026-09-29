using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Jane.Core.Text;

/// <summary>How a number that was spoken is written.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NumberStyle>))]
public enum NumberStyle
{
    /// <summary>Left exactly as the recogniser wrote it.</summary>
    AsSpoken,

    /// <summary>Every spoken number becomes digits, "one" included.</summary>
    Digits,

    /// <summary>
    /// Default. Digits, except a "one" standing on its own, which is a pronoun or a determiner
    /// far more often than it is a figure: "one of them", "no one", "this one".
    /// </summary>
    DigitsExceptLoneOne,
}

/// <param name="Numbers">How spoken numbers are written.</param>
/// <param name="Symbols">
/// Whether a symbol that was named -- comma, period, dash, slash -- is replaced by the symbol.
/// </param>
public sealed record SpokenFormOptions(
    NumberStyle Numbers = NumberStyle.DigitsExceptLoneOne,
    bool Symbols = true)
{
    public static SpokenFormOptions Default { get; } = new();

    /// <summary>Changes nothing. What the pipeline uses when both settings are off.</summary>
    public static SpokenFormOptions Off { get; } = new(NumberStyle.AsSpoken, Symbols: false);

    public bool IsOff => Numbers == NumberStyle.AsSpoken && !Symbols;
}

/// <summary>
/// Writes numbers and symbols the way they are written, rather than the way they are said.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic, and deliberately not the language model's job. A short clean sentence skips the
/// model, and so does everything dictated while a game holds the GPU; a rule only the model
/// applied would work on some dictations and not others, with nothing on screen to say which.
/// </para>
/// <para>
/// Two passes over the same tokens. Numbers first, so that by the time "three slash four" reaches
/// the symbol pass it is already "3 slash 4" and the slash has figures on both sides.
/// </para>
/// <para>
/// Every rule here errs the same way: when a word might be the name of a thing rather than the
/// thing, it is left as a word. "Add a comma" losing its last word is a worse mistake than
/// "hello comma world" keeping one, because the first deletes something the speaker meant.
/// </para>
/// </remarks>
public static class SpokenForms
{
    private const string LeadCharacters = """([{<"'“‘""";
    private const string TrailCharacters = """.,!?;:)]}>"'”’…""";
    private const string SentenceEnders = ".!?…";

    /// <summary>ASR punctuation that a spoken punctuation mark replaces rather than joins.</summary>
    private const string ReplacedByClosingMark = ".,!?;:…";

    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
    };

    private static readonly Dictionary<string, int> Teens = new(StringComparer.Ordinal)
    {
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90,
    };

    private static readonly Dictionary<string, long> Scales = new(StringComparer.Ordinal)
    {
        ["thousand"] = 1_000,
        ["million"] = 1_000_000,
        ["billion"] = 1_000_000_000,
    };

    private static readonly HashSet<string> OrdinalUnits = new(StringComparer.Ordinal)
    {
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth",
    };

    /// <summary>Words that make the "one" after them a figure: step one, version one.</summary>
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    {
        "number", "step", "version", "chapter", "page", "option", "phase", "level", "part", "item",
        "room", "line", "figure", "section", "task", "point", "rule", "type", "grade", "round",
        "season", "episode", "volume", "port", "channel", "ticket", "issue", "release", "stage",
        "tier", "group", "lane", "gate", "track", "unit", "module", "lesson", "week", "day",
    };

    /// <summary>
    /// Words after "one" that settle it as a word whatever came before: "the ticket one more time".
    /// </summary>
    private static readonly HashSet<string> OneFollowers = new(StringComparer.Ordinal)
    {
        "more", "other", "another", "last", "final", "single", "time", "thing", "way",
    };

    /// <summary>What "one of" is a part of when it follows a label: one of them, one of those.</summary>
    private static readonly HashSet<string> Partitives = new(StringComparer.Ordinal)
    {
        "them", "those", "these", "us", "you", "my", "our", "your", "his", "her", "their", "its",
    };

    /// <summary>Words that join two figures without being one: one or two, one to three.</summary>
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "or", "to", "and", "through",
    };

    private static readonly HashSet<string> TimePrepositions = new(StringComparer.Ordinal)
    {
        "at", "by", "around", "until", "till", "before", "after",
    };

    /// <summary>
    /// Words after which the name of a symbol is being talked about rather than dictated.
    /// </summary>
    private static readonly HashSet<string> MentionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "that", "word", "words", "say", "said", "saying", "called",
        "type", "typed", "oxford", "serial", "extra", "missing", "trailing", "leading", "another",
        "no", "any", "some", "each", "every", "your", "my", "his", "her", "its", "our", "their",
    };

    /// <summary>Words after which "dash" and "slash" are verbs.</summary>
    private static readonly HashSet<string> VerbContext = new(StringComparer.Ordinal)
    {
        "to", "will", "would", "could", "should", "can", "might", "must", "i", "we", "you",
        "they", "he", "she", "gonna", "let's",
    };

    /// <summary>Words after "dash" that make it a verb or a measure: a dash of, dash to.</summary>
    private static readonly HashSet<string> DashFollowers = new(StringComparer.Ordinal)
    {
        "of", "to", "for", "off", "out", "across", "through", "away", "back", "over",
    };

    /// <summary>Words before "period" that make it a stretch of time.</summary>
    private static readonly HashSet<string> PeriodQualifiers = new(StringComparer.Ordinal)
    {
        "time", "trial", "grace", "waiting", "long", "short", "brief", "same", "per", "notice",
        "probation", "cooling", "reporting", "billing", "pay", "class", "first", "second", "third",
        "last", "next", "whole", "entire", "certain", "given", "extended", "rest", "review",
    };

    /// <summary>What may follow "dot" for it to be the dot in a file name or an address.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "io", "ai", "dev", "app", "co", "edu", "gov", "exe", "dll", "msi",
        "json", "xml", "yaml", "yml", "md", "txt", "csv", "pdf", "png", "jpg", "jpeg", "gif",
        "svg", "zip", "cs", "csproj", "sln", "js", "jsx", "ts", "tsx", "py", "rs", "go", "java",
        "html", "css", "sql", "sh", "ps1", "toml", "env", "log", "docx", "xlsx", "pptx", "mp3",
        "mp4", "wav", "xaml", "ini", "bat",
    };

    private enum Kind
    {
        None,
        Zero,
        Unit,
        Teen,
        Tens,

        /// <summary>A hyphenated pair the recogniser wrote as one word: twenty-five.</summary>
        TensUnit,
        Hundred,
        Scale,
        And,

        /// <summary>"Oh" or "o", which is a zero only in the middle of a figure.</summary>
        Oh,
    }

    private enum Attachment
    {
        /// <summary>Against the word before, with a space after: comma, period.</summary>
        Closing,

        /// <summary>Against both neighbours: slash, dash, the dot in a file name.</summary>
        Joining,

        /// <summary>A line break, which replaces the space it was said in.</summary>
        Break,

        /// <summary>A mark that stands on its own: an ellipsis.</summary>
        Standalone,
    }

    public static string Normalise(string text, SpokenFormOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var settings = options ?? SpokenFormOptions.Default;
        if (settings.IsOff || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var tokens = Tokenise(text, out var tail);

        if (settings.Numbers != NumberStyle.AsSpoken)
        {
            tokens = ConvertNumbers(tokens, settings);
        }

        if (settings.Symbols)
        {
            tokens = ConvertSymbols(tokens);
        }

        var builder = new StringBuilder(text.Length);
        foreach (var token in tokens)
        {
            builder.Append(token.Space).Append(token.Lead).Append(token.Core).Append(token.Trail);
        }

        return builder.Append(tail).ToString();
    }

    // ---- Tokens ------------------------------------------------------------------------------

    private sealed class Token
    {
        public string Space { get; set; } = string.Empty;

        public string Lead { get; set; } = string.Empty;

        public string Core { get; set; } = string.Empty;

        public string Trail { get; set; } = string.Empty;

        /// <summary>Written by a conversion, so it is never read again as a spoken word.</summary>
        public bool Produced { get; init; }

        public string Lower => Core.ToLowerInvariant();

        public bool EndsSentence => Trail.AsSpan().IndexOfAny(SentenceEnders) >= 0;

        public bool HasDigit => Core.AsSpan().IndexOfAnyInRange('0', '9') >= 0;
    }

    private static List<Token> Tokenise(string text, out string tail)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < text.Length)
        {
            var spaceStart = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index == text.Length)
            {
                tail = text[spaceStart..];
                return tokens;
            }

            var wordStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            tokens.Add(Split(text[spaceStart..wordStart], text[wordStart..index]));
        }

        tail = string.Empty;
        return tokens;
    }

    private static Token Split(string space, string word)
    {
        var start = 0;
        while (start < word.Length && LeadCharacters.Contains(word[start], StringComparison.Ordinal))
        {
            start++;
        }

        var end = word.Length;
        while (end > start && TrailCharacters.Contains(word[end - 1], StringComparison.Ordinal))
        {
            end--;
        }

        // A word that is nothing but punctuation is kept whole. Splitting it would leave an empty
        // core, and an empty core is indistinguishable from a word that was never there.
        return start == end
            ? new Token { Space = space, Core = word }
            : new Token { Space = space, Lead = word[..start], Core = word[start..end], Trail = word[end..] };
    }

    /// <summary>
    /// Whether the token at <paramref name="index"/> carries on the phrase the one before it began.
    /// </summary>
    /// <remarks>
    /// Punctuation between two words means the recogniser heard a pause, and a pause inside a
    /// figure is the speaker moving on to the next one. "One, two, three" is three numbers.
    /// </remarks>
    private static bool Linked(List<Token> tokens, int index) =>
        index > 0
        && index < tokens.Count
        && tokens[index - 1].Trail.Length == 0
        && tokens[index].Lead.Length == 0
        && tokens[index].Space.Length > 0
        && !tokens[index].Space.Contains('\n', StringComparison.Ordinal);

    // ---- Numbers -----------------------------------------------------------------------------

    private static (Kind Kind, long Value) Classify(Token token)
    {
        if (token.Produced)
        {
            return (Kind.None, 0);
        }

        var word = token.Lower;

        if (Units.TryGetValue(word, out var unit))
        {
            return (Kind.Unit, unit);
        }

        if (Teens.TryGetValue(word, out var teen))
        {
            return (Kind.Teen, teen);
        }

        if (Tens.TryGetValue(word, out var tens))
        {
            return (Kind.Tens, tens);
        }

        if (Scales.TryGetValue(word, out var scale))
        {
            return (Kind.Scale, scale);
        }

        switch (word)
        {
            case "zero":
                return (Kind.Zero, 0);
            case "hundred":
                return (Kind.Hundred, 100);
            case "and":
                return (Kind.And, 0);
            case "oh" or "o":
                return (Kind.Oh, 0);
        }

        var hyphen = word.IndexOf('-', StringComparison.Ordinal);
        if (hyphen > 0
            && Tens.TryGetValue(word[..hyphen], out var left)
            && Units.TryGetValue(word[(hyphen + 1)..], out var right))
        {
            return (Kind.TensUnit, left + right);
        }

        return (Kind.None, 0);
    }

    private static Kind KindAt(List<Token> tokens, int index) =>
        index >= 0 && index < tokens.Count ? Classify(tokens[index]).Kind : Kind.None;

    private static long ValueAt(List<Token> tokens, int index) => Classify(tokens[index]).Value;

    /// <summary>"A" counts as one only in front of the two words it is ever said with.</summary>
    private static bool IsArticleOne(List<Token> tokens, int index)
    {
        if (index >= tokens.Count || tokens[index].Produced || tokens[index].Lower != "a" || !Linked(tokens, index + 1))
        {
            return false;
        }

        var next = Classify(tokens[index + 1]);
        return next.Kind == Kind.Hundred || (next.Kind == Kind.Scale && next.Value == 1_000);
    }

    private static bool IsNumberWord(List<Token> tokens, int index)
    {
        if (index < 0 || index >= tokens.Count)
        {
            return false;
        }

        if (tokens[index].HasDigit)
        {
            return true;
        }

        return KindAt(tokens, index) is Kind.Zero or Kind.Unit or Kind.Teen or Kind.Tens or Kind.TensUnit;
    }

    private static List<Token> ConvertNumbers(List<Token> tokens, SpokenFormOptions options)
    {
        var output = new List<Token>(tokens.Count);
        var index = 0;

        while (index < tokens.Count)
        {
            if (TryReadZeroAfterJoin(tokens, index, options) is { } zero)
            {
                output.Add(zero);
                index++;
                continue;
            }

            if (TryAttachPercent(tokens, index) is { } percent)
            {
                output.Add(percent);
                index += 2;
                continue;
            }

            var number = ReadNumber(tokens, index, output, options);
            if (number is null)
            {
                output.Add(tokens[index]);
                index++;
                continue;
            }

            if (number.Text is null)
            {
                // Recognised as a number and deliberately left alone: copy it through untouched,
                // so its second word is not picked up as a number in its own right.
                for (var i = 0; i < number.Length; i++)
                {
                    output.Add(tokens[index + i]);
                }
            }
            else
            {
                output.Add(new Token
                {
                    Space = tokens[index].Space,
                    Lead = tokens[index].Lead,
                    Core = number.Text,
                    Trail = tokens[index + number.Length - 1].Trail,
                    Produced = true,
                });
            }

            index += number.Length;
        }

        return output;
    }

    /// <param name="Text">What to write, or null to leave the words exactly as they were.</param>
    /// <param name="Length">How many tokens the number took up.</param>
    private sealed record NumberRead(string? Text, int Length);

    private static NumberRead? ReadNumber(List<Token> tokens, int index, List<Token> output, SpokenFormOptions options)
    {
        var kind = KindAt(tokens, index);
        var article = IsArticleOne(tokens, index);

        if (!article && kind is not (Kind.Zero or Kind.Unit or Kind.Teen or Kind.Tens or Kind.TensUnit))
        {
            return null;
        }

        var previous = output.Count > 0 ? output[^1].Lower : string.Empty;

        // Order matters. A time and a run of digits can start with the same words -- "nine oh
        // five" -- and only what surrounds them says which it is, so the more specific reading is
        // tried first.
        if (ReadTime(tokens, index, previous) is { } time)
        {
            return time;
        }

        if (ReadDigitRun(tokens, index) is { } run)
        {
            return WithDecimal(tokens, index, run.Text!, run.Length);
        }

        if (ReadYear(tokens, index) is { } year)
        {
            return year;
        }

        if (ReadShortHundreds(tokens, index) is { } hundreds)
        {
            return hundreds;
        }

        if (!TryReadCardinal(tokens, index, out var value, out var length, out var roundScale))
        {
            return null;
        }

        var lastKind = KindAt(tokens, index + length - 1);
        if (lastKind is Kind.Tens or Kind.Hundred or Kind.Scale
            && Linked(tokens, index + length)
            && OrdinalUnits.Contains(tokens[index + length].Lower))
        {
            // "Twenty first". Half of it in digits is worse than either whole form.
            return new NumberRead(null, length + 1);
        }

        var text = roundScale is null
            ? Format(value)
            : $"{Format(value / Scales[roundScale])} {roundScale}";

        var read = WithDecimal(tokens, index, text, length);
        read = WithUnit(tokens, index, read);

        var isLoneOne = value == 1 && length == 1 && read.Length == 1 && !article;
        if (isLoneOne && !OneIsAFigure(tokens, index, previous, options))
        {
            return new NumberRead(null, 1);
        }

        return read;
    }

    /// <summary>
    /// Whether a "one" on its own is being used as the number rather than the word.
    /// </summary>
    private static bool OneIsAFigure(List<Token> tokens, int index, string previous, SpokenFormOptions options)
    {
        if (options.Numbers == NumberStyle.Digits)
        {
            return true;
        }

        var next = index + 1 < tokens.Count ? tokens[index + 1].Lower : string.Empty;
        var afterNext = index + 2 < tokens.Count ? tokens[index + 2].Lower : string.Empty;

        if (OneFollowers.Contains(next) || (next == "of" && Partitives.Contains(afterNext)))
        {
            return false;
        }

        if (Labels.Contains(previous))
        {
            return true;
        }

        if (index + 1 < tokens.Count && tokens[index].Trail.Length == 0 && IsMeridiem(tokens[index + 1]))
        {
            return true;
        }

        // Counting, a range, or a fraction: a figure on either side makes this one a figure too.
        // Punctuation does not break this the way it breaks a number, because "one, two, three"
        // is exactly the case it is for.
        return IsFigureBeside(tokens, index, -1, options) || IsFigureBeside(tokens, index, +1, options);
    }

    private static bool IsFigureBeside(List<Token> tokens, int index, int direction, SpokenFormOptions options)
    {
        var neighbour = index + direction;
        if (neighbour < 0 || neighbour >= tokens.Count)
        {
            return false;
        }

        if (IsNumberWord(tokens, neighbour))
        {
            return true;
        }

        var word = tokens[neighbour].Lower;
        if (Connectors.Contains(word))
        {
            return IsNumberWord(tokens, neighbour + direction);
        }

        // Across a symbol, "o" counts as well: nobody says "one dash o" meaning the letter.
        return options.Symbols
            && word is "slash" or "dash" or "hyphen"
            && (IsNumberWord(tokens, neighbour + direction) || KindAt(tokens, neighbour + direction) == Kind.Oh);
    }

    /// <summary>
    /// "One dash o": an "o" straight after a symbol that itself follows a figure is a zero.
    /// </summary>
    private static Token? TryReadZeroAfterJoin(List<Token> tokens, int index, SpokenFormOptions options)
    {
        if (!options.Symbols || index < 2 || KindAt(tokens, index) != Kind.Oh)
        {
            return null;
        }

        var joiner = tokens[index - 1].Lower;
        if (joiner is not ("slash" or "dash" or "hyphen") || !IsNumberWord(tokens, index - 2))
        {
            return null;
        }

        var source = tokens[index];
        return new Token { Space = source.Space, Lead = source.Lead, Core = "0", Trail = source.Trail, Produced = true };
    }

    /// <summary>Digits the recogniser already wrote, followed by a "percent" it did not.</summary>
    private static Token? TryAttachPercent(List<Token> tokens, int index)
    {
        var token = tokens[index];
        if (token.Produced
            || !Linked(tokens, index + 1)
            || tokens[index + 1].Lower != "percent"
            || token.Core.Length == 0
            || !token.Core.All(static c => char.IsAsciiDigit(c) || c is '.' or ','))
        {
            return null;
        }

        return new Token
        {
            Space = token.Space,
            Lead = token.Lead,
            Core = token.Core + "%",
            Trail = tokens[index + 1].Trail,
            Produced = true,
        };
    }

    private static NumberRead? ReadTime(List<Token> tokens, int index, string previous)
    {
        var hourKind = KindAt(tokens, index);
        if (hourKind is not (Kind.Unit or Kind.Teen))
        {
            return null;
        }

        var hour = ValueAt(tokens, index);
        if (hour is < 1 or > 12 || !Linked(tokens, index + 1))
        {
            return null;
        }

        long minutes;
        int length;

        if (KindAt(tokens, index + 1) == Kind.Oh && Linked(tokens, index + 2) && KindAt(tokens, index + 2) == Kind.Unit)
        {
            minutes = ValueAt(tokens, index + 2);
            length = 3;
        }
        else if (TryReadBelowHundred(tokens, index + 1, out minutes, out var taken) && minutes is >= 10 and <= 59)
        {
            length = 1 + taken;
        }
        else
        {
            return null;
        }

        var last = index + length - 1;
        var hasMeridiem = last + 1 < tokens.Count && tokens[last].Trail.Length == 0 && IsMeridiem(tokens[last + 1]);

        return hasMeridiem || TimePrepositions.Contains(previous)
            ? new NumberRead(string.Create(CultureInfo.InvariantCulture, $"{hour}:{minutes:00}"), length)
            : null;
    }

    private static bool IsMeridiem(Token token) =>
        !token.Produced && token.Lower.Replace(".", string.Empty, StringComparison.Ordinal) is "am" or "pm";

    /// <summary>
    /// Three or more digits read out one at a time are one number: a ticket, a port, a code.
    /// </summary>
    /// <remarks>
    /// Three, not two. "Three four seconds" is somebody saying "three or four" quickly, and it was
    /// dictated more than once; "one eight eight" is never anything but 188.
    /// </remarks>
    private static NumberRead? ReadDigitRun(List<Token> tokens, int index)
    {
        var digits = new StringBuilder();
        var position = index;

        while (position < tokens.Count && (position == index || Linked(tokens, position)))
        {
            var kind = KindAt(tokens, position);
            if (kind is Kind.Unit or Kind.Zero)
            {
                digits.Append(ValueAt(tokens, position));
            }
            else if (kind == Kind.Oh && position > index)
            {
                digits.Append('0');
            }
            else
            {
                break;
            }

            position++;
        }

        return digits.Length >= 3 ? new NumberRead(digits.ToString(), position - index) : null;
    }

    /// <summary>A year said as two pairs: nineteen ninety nine, twenty twenty six, twenty oh five.</summary>
    private static NumberRead? ReadYear(List<Token> tokens, int index)
    {
        if (!TryReadBelowHundred(tokens, index, out var century, out var first) || century is < 13 or > 20)
        {
            return null;
        }

        var second = index + first;
        if (!Linked(tokens, second))
        {
            return null;
        }

        if (KindAt(tokens, second) == Kind.Oh && Linked(tokens, second + 1) && KindAt(tokens, second + 1) == Kind.Unit)
        {
            return new NumberRead(
                string.Create(CultureInfo.InvariantCulture, $"{century}0{ValueAt(tokens, second + 1)}"),
                first + 2);
        }

        return TryReadBelowHundred(tokens, second, out var rest, out var taken) && rest >= 10
            ? new NumberRead(string.Create(CultureInfo.InvariantCulture, $"{century}{rest}"), first + taken)
            : null;
    }

    /// <summary>
    /// "Three twenty" for 320: the hundred left unsaid, the way ticket and room numbers are read.
    /// </summary>
    /// <remarks>
    /// Found in real dictation, where it came out as "3 20". A digit followed by a separate
    /// two-digit number is almost never two numbers; when it is a time, the words around it say
    /// so and that reading has already been tried.
    /// </remarks>
    private static NumberRead? ReadShortHundreds(List<Token> tokens, int index)
    {
        if (KindAt(tokens, index) != Kind.Unit
            || !Linked(tokens, index + 1)
            || !TryReadBelowHundred(tokens, index + 1, out var rest, out var taken)
            || rest < 10)
        {
            return null;
        }

        var after = index + 1 + taken;
        if (Linked(tokens, after) && KindAt(tokens, after) is Kind.Hundred or Kind.Scale)
        {
            return null;
        }

        return new NumberRead(
            string.Create(CultureInfo.InvariantCulture, $"{ValueAt(tokens, index)}{rest}"),
            1 + taken);
    }

    private static bool TryReadBelowHundred(List<Token> tokens, int index, out long value, out int length)
    {
        value = 0;
        length = 0;

        var (kind, number) = index < tokens.Count ? Classify(tokens[index]) : (Kind.None, 0);

        switch (kind)
        {
            case Kind.Unit or Kind.Teen or Kind.TensUnit:
                value = number;
                length = 1;
                return true;

            case Kind.Tens:
                value = number;
                length = 1;
                if (Linked(tokens, index + 1) && KindAt(tokens, index + 1) == Kind.Unit)
                {
                    value += ValueAt(tokens, index + 1);
                    length = 2;
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>One group of up to three digits: "one hundred and eighty nine".</summary>
    private static bool TryReadGroup(List<Token> tokens, int index, out long value, out int length)
    {
        value = 0;
        length = 0;

        var kind = KindAt(tokens, index);
        var article = IsArticleOne(tokens, index);
        var leadsHundred = (article || kind is Kind.Unit or Kind.Teen)
            && Linked(tokens, index + 1)
            && KindAt(tokens, index + 1) == Kind.Hundred;

        if (!leadsHundred)
        {
            return !article && TryReadBelowHundred(tokens, index, out value, out length);
        }

        value = (article ? 1 : ValueAt(tokens, index)) * 100;
        length = 2;

        var next = index + 2;
        if (Linked(tokens, next) && KindAt(tokens, next) == Kind.And && Linked(tokens, next + 1)
            && TryReadBelowHundred(tokens, next + 1, out var afterAnd, out var takenAfterAnd))
        {
            value += afterAnd;
            length += 1 + takenAfterAnd;
        }
        else if (Linked(tokens, next) && TryReadBelowHundred(tokens, next, out var rest, out var taken))
        {
            value += rest;
            length += taken;
        }

        return true;
    }

    /// <param name="roundScale">
    /// Set when the number is a small multiple of a million or a billion and nothing else, which
    /// is written "2 million" rather than as a row of noughts.
    /// </param>
    private static bool TryReadCardinal(
        List<Token> tokens, int index, out long value, out int length, out string? roundScale)
    {
        value = 0;
        length = 0;
        roundScale = null;

        if (KindAt(tokens, index) == Kind.Zero)
        {
            length = 1;
            return true;
        }

        long pending;
        int position;

        if (IsArticleOne(tokens, index) && KindAt(tokens, index + 1) == Kind.Scale)
        {
            pending = 1;
            position = index + 1;
        }
        else if (TryReadGroup(tokens, index, out pending, out var taken))
        {
            position = index + taken;
        }
        else
        {
            return false;
        }

        long total = 0;
        var lowestScale = long.MaxValue;
        var scalesUsed = 0;
        string? lastScaleWord = null;
        long lastMultiplier = 0;

        while (Linked(tokens, position) && KindAt(tokens, position) == Kind.Scale)
        {
            var scale = ValueAt(tokens, position);
            if (scale >= lowestScale || pending == 0)
            {
                break;
            }

            total += pending * scale;
            lowestScale = scale;
            lastScaleWord = tokens[position].Lower;
            lastMultiplier = pending;
            scalesUsed++;
            pending = 0;
            position++;

            var group = position;
            if (Linked(tokens, group) && KindAt(tokens, group) == Kind.And)
            {
                group++;
            }

            if (Linked(tokens, group) && TryReadGroup(tokens, group, out var next, out var takenNext))
            {
                pending = next;
                position = group + takenNext;
            }
        }

        value = total + pending;
        length = position - index;

        if (scalesUsed == 1 && pending == 0 && lowestScale >= 1_000_000 && lastMultiplier < 1_000)
        {
            roundScale = lastScaleWord;
        }

        return true;
    }

    private static NumberRead WithDecimal(List<Token> tokens, int index, string whole, int length)
    {
        var point = index + length;
        if (!Linked(tokens, point) || tokens[point].Lower != "point" || tokens[point].Produced)
        {
            return new NumberRead(whole, length);
        }

        var fraction = new StringBuilder();
        var position = point + 1;
        while (Linked(tokens, position) && KindAt(tokens, position) is Kind.Unit or Kind.Zero or Kind.Oh)
        {
            fraction.Append(ValueAt(tokens, position));
            position++;
        }

        return fraction.Length == 0
            ? new NumberRead(whole, length)
            : new NumberRead($"{whole}.{fraction}", position - index);
    }

    private static NumberRead WithUnit(List<Token> tokens, int index, NumberRead read)
    {
        var next = index + read.Length;
        if (!Linked(tokens, next) || tokens[next].Produced)
        {
            return read;
        }

        switch (tokens[next].Lower)
        {
            case "percent":
                return new NumberRead(read.Text + "%", read.Length + 1);

            case "dollar" or "dollars":
                var cents = next + 1;
                if (Linked(tokens, cents) && KindAt(tokens, cents) == Kind.And
                    && Linked(tokens, cents + 1)
                    && TryReadBelowHundred(tokens, cents + 1, out var amount, out var taken)
                    && Linked(tokens, cents + 1 + taken)
                    && tokens[cents + 1 + taken].Lower is "cent" or "cents")
                {
                    return new NumberRead(
                        string.Create(CultureInfo.InvariantCulture, $"${read.Text}.{amount:00}"),
                        read.Length + 3 + taken);
                }

                return new NumberRead("$" + read.Text, read.Length + 1);

            default:
                return read;
        }
    }

    /// <summary>
    /// Plain below ten thousand, grouped above it.
    /// </summary>
    /// <remarks>
    /// Four digits are left bare because most four-digit numbers anyone dictates are years, ports
    /// and ticket numbers, and "2,026" is wrong for every one of them.
    /// </remarks>
    private static string Format(long value) =>
        value < 10_000
            ? value.ToString(CultureInfo.InvariantCulture)
            : value.ToString("N0", CultureInfo.InvariantCulture);

    // ---- Symbols -----------------------------------------------------------------------------

    /// <param name="Length">How many tokens the name of the symbol took up.</param>
    private sealed record SymbolRead(string Text, Attachment Attachment, int Length, bool EndsSentence = false);

    private static List<Token> ConvertSymbols(List<Token> tokens)
    {
        var output = new List<Token>(tokens.Count);
        var glueNext = false;
        var capitaliseNext = false;
        string? spaceForNext = null;

        var index = 0;
        while (index < tokens.Count)
        {
            var previous = output.Count > 0 ? output[^1] : null;
            var symbol = ReadSymbol(tokens, index, previous);

            if (symbol is null)
            {
                var token = tokens[index];

                if (spaceForNext is not null)
                {
                    token.Space = spaceForNext;
                }
                else if (glueNext)
                {
                    token.Space = string.Empty;
                }

                if (capitaliseNext && token.Lead.Length == 0 && token.Core.Length > 0 && char.IsLower(token.Core[0]))
                {
                    token.Core = char.ToUpperInvariant(token.Core[0]) + token.Core[1..];
                }

                output.Add(token);
                glueNext = false;
                capitaliseNext = false;
                spaceForNext = null;
                index++;
                continue;
            }

            var first = tokens[index];
            var last = tokens[index + symbol.Length - 1];
            var space = spaceForNext ?? (glueNext ? string.Empty : first.Space);

            // Whatever the recogniser hung on the name of the symbol was its guess at a pause.
            // The symbol is the answer; only a closing bracket or quote is worth keeping.
            var kept = Without(last.Trail, ReplacedByClosingMark);

            switch (symbol.Attachment)
            {
                case Attachment.Closing:
                    if (previous is not null)
                    {
                        previous.Trail = Without(previous.Trail, ReplacedByClosingMark) + symbol.Text + kept;
                    }
                    else
                    {
                        output.Add(new Token { Space = space, Core = symbol.Text, Trail = kept, Produced = true });
                    }

                    glueNext = false;
                    spaceForNext = null;
                    capitaliseNext = symbol.EndsSentence;
                    break;

                case Attachment.Joining:
                    if (previous is not null && !previous.EndsSentence && !IsMentionOf(symbol, previous))
                    {
                        previous.Trail = Without(previous.Trail, ",") + symbol.Text;
                    }
                    else
                    {
                        output.Add(new Token { Space = space, Core = symbol.Text, Produced = true });
                    }

                    // A sentence that ends on the symbol -- "AI dash." -- ends there.
                    if (last.EndsSentence && index + symbol.Length >= tokens.Count)
                    {
                        output[^1].Trail += Without(last.Trail, ",");
                    }

                    glueNext = true;
                    spaceForNext = null;
                    break;

                case Attachment.Break:
                    if (index + symbol.Length >= tokens.Count)
                    {
                        output.Add(new Token { Core = symbol.Text, Produced = true });
                    }

                    spaceForNext = symbol.Text;
                    glueNext = false;
                    capitaliseNext = true;
                    break;

                default:
                    output.Add(new Token { Space = space, Core = symbol.Text, Trail = last.Trail, Produced = true });
                    glueNext = false;
                    spaceForNext = null;
                    break;
            }

            index += symbol.Length;
        }

        return output;
    }

    /// <summary>
    /// A dot that follows "the" is still a dot -- "the .exe file" -- it just does not join.
    /// </summary>
    private static bool IsMentionOf(SymbolRead symbol, Token previous) =>
        symbol.Text == "." && MentionWords.Contains(previous.Lower);

    private static string Without(string text, string characters)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!characters.Contains(character, StringComparison.Ordinal))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool Phrase(List<Token> tokens, int index, params string[] words)
    {
        if (index + words.Length > tokens.Count)
        {
            return false;
        }

        for (var i = 0; i < words.Length; i++)
        {
            var token = tokens[index + i];
            if (token.Produced || token.Lower != words[i] || (i > 0 && !Linked(tokens, index + i)))
            {
                return false;
            }
        }

        return true;
    }

    private static SymbolRead? ReadSymbol(List<Token> tokens, int index, Token? previous)
    {
        if (tokens[index].Produced)
        {
            return null;
        }

        var symbol = Recognise(tokens, index);
        if (symbol is null)
        {
            return null;
        }

        var before = previous?.Lower ?? string.Empty;
        var afterIndex = index + symbol.Length;
        var after = afterIndex < tokens.Count ? tokens[afterIndex].Lower : string.Empty;
        var word = tokens[index].Lower;

        if (word == "dot")
        {
            // The one symbol a determiner does not rule out, because what follows it already
            // proved what it is.
            return symbol;
        }

        if (MentionWords.Contains(before))
        {
            return null;
        }

        // "Dash or slash or comma" is a list of names. "And slash or" is the exception that is
        // said more than every other use of the word put together.
        if (before == "or" || (after == "or" && !(word == "slash" && before == "and")))
        {
            return null;
        }

        switch (word)
        {
            case "slash" when VerbContext.Contains(before):
            case "dash" or "hyphen" when VerbContext.Contains(before) || DashFollowers.Contains(after):
            case "period" when PeriodQualifiers.Contains(before) || after == "of":
                return null;

            default:
                return symbol;
        }
    }

    private static SymbolRead? Recognise(List<Token> tokens, int index)
    {
        if (Phrase(tokens, index, "dot", "dot", "dot"))
        {
            return new SymbolRead("...", Attachment.Standalone, 3);
        }

        if (Phrase(tokens, index, "question", "mark"))
        {
            return new SymbolRead("?", Attachment.Closing, 2, EndsSentence: true);
        }

        if (Phrase(tokens, index, "exclamation", "mark") || Phrase(tokens, index, "exclamation", "point"))
        {
            return new SymbolRead("!", Attachment.Closing, 2, EndsSentence: true);
        }

        if (Phrase(tokens, index, "full", "stop"))
        {
            return new SymbolRead(".", Attachment.Closing, 2, EndsSentence: true);
        }

        if (Phrase(tokens, index, "forward", "slash"))
        {
            return new SymbolRead("/", Attachment.Joining, 2);
        }

        if (Phrase(tokens, index, "back", "slash"))
        {
            return new SymbolRead("\\", Attachment.Joining, 2);
        }

        if (Phrase(tokens, index, "semi", "colon"))
        {
            return new SymbolRead(";", Attachment.Closing, 2);
        }

        if (Phrase(tokens, index, "new", "line"))
        {
            return new SymbolRead("\n", Attachment.Break, 2);
        }

        if (Phrase(tokens, index, "new", "paragraph"))
        {
            return new SymbolRead("\n\n", Attachment.Break, 2);
        }

        return tokens[index].Lower switch
        {
            "comma" => new SymbolRead(",", Attachment.Closing, 1),
            "period" => new SymbolRead(".", Attachment.Closing, 1, EndsSentence: true),
            "colon" => new SymbolRead(":", Attachment.Closing, 1),
            "semicolon" => new SymbolRead(";", Attachment.Closing, 1),
            "slash" => new SymbolRead("/", Attachment.Joining, 1),
            "backslash" => new SymbolRead("\\", Attachment.Joining, 1),
            "dash" or "hyphen" => new SymbolRead("-", Attachment.Joining, 1),
            "dot" when index + 1 < tokens.Count
                && Linked(tokens, index + 1)
                && !tokens[index + 1].Produced
                && Extensions.Contains(tokens[index + 1].Lower) => new SymbolRead(".", Attachment.Joining, 1),
            _ => null,
        };
    }
}
