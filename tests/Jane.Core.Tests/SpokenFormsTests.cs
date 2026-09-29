using Jane.Core.Text;

namespace Jane.Core.Tests;

/// <summary>
/// Whether a number or a symbol that was spoken arrives as the thing itself.
/// </summary>
/// <remarks>
/// <para>
/// The recogniser writes what it heard. "Ticket one eight eight" came out as three words, "three
/// slash four" as three more, and the cleanup prompt was told to leave number forms exactly as
/// spoken -- so every figure in a dictation had to be retyped by hand.
/// </para>
/// <para>
/// This is code rather than a prompt for one reason: a short clean sentence never reaches the
/// language model at all, and neither does anything dictated while a game holds the GPU. A rule
/// that only the model applied would work some of the time, which is worse than not at all.
/// </para>
/// <para>
/// Every example below that is not invented is a phrase that was actually dictated.
/// </para>
/// </remarks>
public sealed class SpokenFormsTests
{
    private static string Normalise(string text) => SpokenForms.Normalise(text, SpokenFormOptions.Default);

    [Theory]
    [InlineData("There are two notes I have.", "There are 2 notes I have.")]
    [InlineData("wait for like ten seconds", "wait for like 10 seconds")]
    [InlineData("I do forty pull-ups", "I do 40 pull-ups")]
    [InlineData("it costs twenty five", "it costs 25")]
    [InlineData("it costs twenty-five", "it costs 25")]
    [InlineData("start on ticket one hundred eighty nine, which has", "start on ticket 189, which has")]
    [InlineData("one hundred and five people", "105 people")]
    [InlineData("two thousand twenty six", "2026")]
    [InlineData("three thousand", "3000")]
    [InlineData("one hundred fifty thousand", "150,000")]
    [InlineData("Zero errors.", "0 errors.")]
    public void ANumberThatWasSpokenIsWrittenInDigits(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("follow the plan for one eight eight fully", "follow the plan for 188 fully")]
    [InlineData("server running on five zero zero one.", "server running on 5001.")]
    [InlineData("my ticket for one eight three.", "my ticket for 183.")]
    [InlineData("the code is four oh four", "the code is 404")]
    [InlineData("the planning for three twenty.", "the planning for 320.")]
    [InlineData("let's wrap up three twenty three", "let's wrap up 323")]
    [InlineData("the PR for two ten", "the PR for 210")]
    public void DigitsReadOutOneAtATimeAreOneNumber(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("Testing, testing, one, two, three.", "Testing, testing, 1, 2, 3.")]
    [InlineData("going for another like three four seconds.", "going for another like 3 4 seconds.")]
    public void NumbersThatWereSaidSeparatelyStaySeparate(string spoken, string expected)
    {
        // A pause the recogniser heard as a comma is the speaker counting, not reading a figure.
        // And two digits on their own are far more often "three or four" than "thirty-four".
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("nineteen ninety nine", "1999")]
    [InlineData("back in twenty twenty six", "back in 2026")]
    [InlineData("twenty ten", "2010")]
    [InlineData("twenty oh five", "2005")]
    public void AYearIsReadAsTwoPairs(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("One note is it didn't work", "One note is it didn't work")]
    [InlineData("But again, one of my main uses", "But again, one of my main uses")]
    [InlineData("I can manually start one myself?", "I can manually start one myself?")]
    [InlineData("no one knows", "no one knows")]
    [InlineData("such as this one to see", "such as this one to see")]
    [InlineData("review the ticket one more time before", "review the ticket one more time before")]
    [InlineData("step one of the plan", "step 1 of the plan")]
    [InlineData("that is day one of many", "that is day 1 of many")]
    [InlineData("on the day one of them left", "on the day one of them left")]
    public void OneOnItsOwnIsAWordNotAFigure(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("Just one or two questions.", "Just 1 or 2 questions.")]
    [InlineData("one to three days", "1 to 3 days")]
    [InlineData("step one is the build", "step 1 is the build")]
    [InlineData("version one is out", "version 1 is out")]
    [InlineData("one percent", "1%")]
    [InlineData("see you at one pm", "see you at 1 pm")]
    public void OneIsAFigureWhenItIsPlainlyBeingUsedAsOne(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Fact]
    public void OneCanBeMadeAFigureEverywhere()
    {
        var options = new SpokenFormOptions(NumberStyle.Digits);

        Assert.Equal("1 note is missing", SpokenForms.Normalise("One note is missing", options));
    }

    [Fact]
    public void NumbersCanBeLeftExactlyAsSpoken()
    {
        var options = new SpokenFormOptions(NumberStyle.AsSpoken);

        Assert.Equal("twenty five percent", SpokenForms.Normalise("twenty five percent", options));
    }

    [Theory]
    [InlineData("the Jane one point oh build", "the Jane 1.0 build")]
    [InlineData("is the Jane One point O which is the", "is the Jane 1.0 which is the")]
    [InlineData("three point one four", "3.14")]
    [InlineData("zero point five", "0.5")]
    [InlineData("There is no point in duplicating work", "There is no point in duplicating work")]
    [InlineData("related to point two.", "related to point 2.")]
    public void PointBetweenFiguresIsADecimalPoint(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("It wasn't a hundred percent accurate", "It wasn't 100% accurate")]
    [InlineData("at eighty percent volume", "at 80% volume")]
    [InlineData("twenty dollars", "$20")]
    [InlineData("five dollars and fifty cents", "$5.50")]
    [InlineData("two million users", "2 million users")]
    [InlineData("a million reasons", "a million reasons")]
    public void UnitsAttachTheWayTheyAreWritten(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("three thirty pm", "3:30 pm")]
    [InlineData("meet at three thirty", "meet at 3:30")]
    [InlineData("by nine oh five a.m.", "by 9:05 a.m.")]
    [InlineData("three PM works", "3 PM works")]
    public void ATimeIsWrittenWithAColon(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("the twenty first of May", "the twenty first of May")]
    [InlineData("the first two links", "the first 2 links")]
    [InlineData("wait a second", "wait a second")]
    public void OrdinalsAreLeftAsTheyWereSaid(string spoken, string expected)
    {
        // "Second" is a unit of time far more often than it is 2nd, and half an ordinal in digits
        // -- "20 first" -- is worse than either form.
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("My ticket for 188 was to handle", "My ticket for 188 was to handle")]
    [InlineData("a Friday at 80% volume.", "a Friday at 80% volume.")]
    [InlineData("one-on-one meeting", "one-on-one meeting")]
    [InlineData("my three-year-old", "my three-year-old")]
    [InlineData("anyone can do it", "anyone can do it")]
    public void WhatIsAlreadyRightIsNotTouched(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("three slash four", "3/4")]
    [InlineData("So something like three dash four", "So something like 3-4")]
    [InlineData("research mainly on X slash Twitter?", "research mainly on X/Twitter?")]
    [InlineData("and slash or", "and/or")]
    [InlineData("AI dash one eight nine", "AI-189")]
    [InlineData("AI, dash, one eight nine.", "AI-189.")]
    [InlineData("or one dash o or whatever", "or 1-0 or whatever")]
    [InlineData("slash compact", "/compact")]
    [InlineData("C colon backslash users", "C:\\users")]
    [InlineData("src forward slash main", "src/main")]
    public void ASymbolSaidBetweenTwoThingsJoinsThem(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("Hello comma world", "Hello, world")]
    [InlineData("Hello, comma, world.", "Hello, world.")]
    [InlineData("ship it period", "ship it.")]
    [InlineData("Ship it period. Then call me.", "Ship it. Then call me.")]
    [InlineData("ship it full stop then call me", "ship it. Then call me")]
    [InlineData("is that right question mark", "is that right?")]
    [InlineData("stop exclamation mark", "stop!")]
    [InlineData("two things colon speed and size", "2 things: speed and size")]
    [InlineData("comma great vertical jumping ability.", ", great vertical jumping ability.")]
    [InlineData("Period.", ".")]
    public void PunctuationSaidOutLoudSitsAgainstTheWordBeforeIt(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("just separated by a comma.", "just separated by a comma.")]
    [InlineData("add the period at the end", "add the period at the end")]
    [InlineData("a dash of salt", "a dash of salt")]
    [InlineData("I need to dash to the store", "I need to dash to the store")]
    [InlineData("they will slash prices", "they will slash prices")]
    [InlineData("over a long period of time", "over a long period of time")]
    [InlineData("the trial period ended", "the trial period ended")]
    [InlineData("it needs a question mark", "it needs a question mark")]
    [InlineData(
        "if I say the word dash or slash or comma or period that it uses the symbols",
        "if I say the word dash or slash or comma or period that it uses the symbols")]
    public void TheNameOfASymbolIsStillAWord(string spoken, string expected)
    {
        // Talking about a comma is not the same as dictating one. Getting this wrong deletes a
        // word the speaker meant, which is a worse error than leaving a symbol spelled out.
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("download the setup dot exe from there.", "download the setup.exe from there.")]
    [InlineData("specific icon for the dot exe file.", "specific icon for the .exe file.")]
    [InlineData("go to example dot com", "go to example.com")]
    [InlineData("I click on the dot dot dot menu", "I click on the ... menu")]
    [InlineData("a polka dot dress", "a polka dot dress")]
    public void DotIsAFullStopOnlyInsideAFileNameOrAnAddress(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Theory]
    [InlineData("first line new line second line", "first line\nSecond line")]
    [InlineData("intro new paragraph body", "intro\n\nBody")]
    [InlineData("start a new line here", "start a new line here")]
    public void ANewLineIsALineBreak(string spoken, string expected)
    {
        Assert.Equal(expected, Normalise(spoken));
    }

    [Fact]
    public void SymbolsCanBeLeftExactlyAsSpoken()
    {
        var options = new SpokenFormOptions(Symbols: false);

        Assert.Equal("3 slash 4", SpokenForms.Normalise("three slash four", options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hello there.")]
    public void TextWithNothingToConvertComesBackUnchanged(string text)
    {
        Assert.Equal(text, Normalise(text));
    }

    [Theory]
    [InlineData("start on ticket one hundred eighty nine, which has")]
    [InlineData("three slash four")]
    [InlineData("Hello comma world period")]
    [InlineData("the Jane one point oh build at three thirty pm")]
    public void ConvertingTwiceChangesNothingMore(string spoken)
    {
        // The pipeline may pass text through here again after the language model has had it.
        var once = Normalise(spoken);

        Assert.Equal(once, Normalise(once));
    }
}
