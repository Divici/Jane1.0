namespace Jane.Core.Text;

/// <summary>
/// Decides whether an injection needs a space in front of it.
/// </summary>
/// <remarks>
/// <para>
/// Jane types at the caret and contributes nothing of its own, which is right for a single
/// dictation and wrong for the second one. Dictating a sentence, pausing, and dictating the next
/// produced <c>Hello there.How are you?</c>: every sentence after the first arrived glued to its
/// predecessor, and fixing it meant reaching for the keyboard.
/// </para>
/// <para>
/// The obvious fix -- look at the character before the caret -- is not available. UIA returns the
/// enclosing paragraph with no caret offset inside it, getting one costs round trips inside an
/// 80ms budget, and many of the applications people dictate into serve no UIA text at all. So this
/// works from what Jane knows for certain instead: the text it last injected into this very
/// window. That covers the case that actually goes wrong, and everywhere the memory does not
/// apply the policy declines to guess rather than inventing a space.
/// </para>
/// <para>
/// The asymmetry is deliberate. A missing space is silent and has to be repaired by hand; a
/// spurious one is visible the moment it appears. Where the two are genuinely in doubt this
/// prefers to add nothing.
/// </para>
/// </remarks>
public static class SpacingPolicy
{
    /// <summary>
    /// Characters that bind to whatever follows them, where a space would break the meaning
    /// rather than restore it: brackets and quotes that were just opened, path separators, an
    /// email's "@", a hyphen mid-compound.
    /// </summary>
    private const string BindsForward = """([{<"'“‘/\@#$_~-–—""";

    /// <summary>
    /// Characters that belong hard against the word before them. Dictating "period" on its own is
    /// the common case; closing brackets, quotes and a possessive "'s" are the same shape.
    /// </summary>
    private const string BindsBackward = """.,!?;:)]}>"'”’%…""";

    /// <summary>
    /// The text to inject, with a separating space if one is missing.
    /// </summary>
    /// <param name="text">The formatted dictation, about to go to the injector.</param>
    /// <param name="previous">
    /// What Jane last injected into this same window, or <see langword="null"/> when there is no
    /// such memory -- a different window, a fresh session, or an injection that failed. Null means
    /// "unknown", and unknown means leave the text alone.
    /// </param>
    public static string Apply(string text, string? previous)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(previous))
        {
            return text;
        }

        // Already separated on one side or the other. Two spaces is a bug in the opposite
        // direction, and just as visible.
        if (char.IsWhiteSpace(previous[^1]) || char.IsWhiteSpace(text[0]))
        {
            return text;
        }

        if (BindsForward.Contains(previous[^1], StringComparison.Ordinal) ||
            BindsBackward.Contains(text[0], StringComparison.Ordinal))
        {
            return text;
        }

        return " " + text;
    }
}
