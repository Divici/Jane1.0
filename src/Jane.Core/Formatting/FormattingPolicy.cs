using Jane.Core.Abstractions;

namespace Jane.Core.Formatting;

/// <param name="Spoken">
/// The form the recogniser actually emits -- the dictionary term itself, or its pronunciation
/// hint. Matching happens on this, not on the replacement.
/// </param>
/// <param name="Replacement">What the user wants written instead.</param>
public sealed record DictionaryReplacement(string Spoken, string Replacement);

/// <summary>
/// Everything the user's own settings contribute to one dictation.
/// </summary>
/// <remarks>
/// <see cref="FormattingContext"/> is fixed at Phase 5 and carries only what the pipeline knows
/// (focused process, Deep Context hints). The dictionary and Custom Instructions arrive from
/// Phase 10's tables instead, and they are the two things that must never be silently skipped --
/// which is why they are resolved once, up front, and handed to both the bypass heuristic and
/// the prompt builder rather than being looked up separately by each.
/// </remarks>
public sealed record FormattingPolicy
{
    /// <summary>Natural-language rules for the focused app, global rules folded in. Aqua's model.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>Only the entries whose spoken form occurs in *this* transcript.</summary>
    public IReadOnlyList<DictionaryReplacement> Replacements { get; init; } = [];

    /// <summary>Terms to spell correctly. Hints, not substitutions -- they never block the bypass.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary>
    /// Forces the LLM to run. Phase 10 sets this from the "always clean up" setting, and the eval
    /// harness sets it to measure what the bypass would have skipped.
    /// </summary>
    public bool SuppressBypass { get; init; }

    public static FormattingPolicy None { get; } = new();

    public bool HasCustomInstructions => !string.IsNullOrWhiteSpace(CustomInstructions);
}

/// <summary>Resolves the settings that apply to one dictation.</summary>
/// <remarks>
/// One seam rather than two: Phase 10 implements this over <c>UserDictionary</c> and
/// <c>CustomInstructions</c> together, and the formatter never learns that a database exists.
/// </remarks>
public interface IFormattingPolicySource
{
    FormattingPolicy Resolve(string transcript, FormattingContext context);
}

/// <summary>The Phase 7 default: no dictionary, no instructions, bypass available.</summary>
public sealed class EmptyFormattingPolicySource : IFormattingPolicySource
{
    public static EmptyFormattingPolicySource Instance { get; } = new();

    public FormattingPolicy Resolve(string transcript, FormattingContext context) => FormattingPolicy.None;
}
