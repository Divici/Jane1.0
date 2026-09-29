using Jane.Core.Text;

namespace Jane.Core.Tests;

/// <summary>
/// Whether consecutive dictations run into each other.
/// </summary>
/// <remarks>
/// <para>
/// Jane injects at the caret and adds nothing of its own, so dictating "Hello there." and then,
/// after a pause, "How are you?" produced <c>Hello there.How are you?</c>. Every sentence after
/// the first arrived glued to the one before it, and the user had to reach for the keyboard to
/// insert a space -- in a tool whose entire purpose is not reaching for the keyboard.
/// </para>
/// <para>
/// The policy itself is a pure function of what is about to be typed and what sits before the
/// caret. Where that second fact comes from -- the application's own answer, or Jane's memory of
/// what it last typed -- is the orchestrator's business and is tested there. Here, null means
/// nobody knows, and not knowing means adding nothing.
/// </para>
/// </remarks>
public sealed class SpacingPolicyTests
{
    [Fact]
    public void ASecondDictationIntoTheSameFieldIsSeparatedFromTheFirst()
    {
        Assert.Equal(" How are you?", SpacingPolicy.Apply("How are you?", "Hello there."));
    }

    [Fact]
    public void TheFirstDictationIntoAFieldIsLeftAlone()
    {
        // Nothing is known about what precedes the caret, so nothing is assumed. A stray leading
        // space in an empty search box is its own bug.
        Assert.Equal("Hello there.", SpacingPolicy.Apply("Hello there.", previous: null));
    }

    [Fact]
    public void TextAlreadySeparatedIsNotSeparatedTwice()
    {
        Assert.Equal("How are you?", SpacingPolicy.Apply("How are you?", "Hello there. "));
    }

    [Fact]
    public void ANewLineIsAlreadyASeparator()
    {
        Assert.Equal("How are you?", SpacingPolicy.Apply("How are you?", "Hello there.\n"));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[")]
    [InlineData("\"")]
    [InlineData("/")]
    [InlineData("@")]
    [InlineData("-")]
    public void NothingIsAddedAfterACharacterThatOpensSomething(string trailing)
    {
        // "(", a path separator or an email's "@" all bind to what follows them. A space here is
        // not a missing-space bug being fixed, it is a new one being introduced.
        Assert.Equal("word", SpacingPolicy.Apply("word", "prefix" + trailing));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(",")]
    [InlineData("?")]
    [InlineData(")")]
    [InlineData("'s")]
    public void NothingIsAddedBeforePunctuationThatAttachesToTheWordBefore(string text)
    {
        // The user said "period" as its own dictation. It belongs against the previous word.
        Assert.Equal(text, SpacingPolicy.Apply(text, "Hello there"));
    }

    [Fact]
    public void TextThatAlreadyCarriesItsOwnLeadingSpaceIsNotDoubled()
    {
        Assert.Equal(" How are you?", SpacingPolicy.Apply(" How are you?", "Hello there."));
    }

    [Fact]
    public void EmptyTextIsNeverTurnedIntoASpace()
    {
        Assert.Equal(string.Empty, SpacingPolicy.Apply(string.Empty, "Hello there."));
    }
}
