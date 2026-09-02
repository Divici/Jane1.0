namespace Jane.App.Onboarding;

/// <summary>
/// The five things first run has to get done, plus the two ends.
/// </summary>
/// <remarks>
/// The order is the order of dependency, not of importance. Models are downloaded before the
/// hotkey is chosen because the download is the long part and choosing a key while 482 MB arrives
/// is wasted waiting; the test dictation is last because it needs everything else to have worked.
/// </remarks>
public enum OnboardingStep
{
    /// <summary>What Jane is, what it keeps, and what it sends. Said before anything is asked.</summary>
    Welcome,

    /// <summary>Pick an input device and confirm Jane can hear it.</summary>
    Microphone,

    /// <summary>Download the speech models and pull the language models. The long step.</summary>
    Models,

    /// <summary>Choose the push-to-talk key, with live conflict detection.</summary>
    Hotkey,

    /// <summary>Actually dictate something, into a box that is right there.</summary>
    TestDictation,

    Done,
}

/// <param name="Summary">
/// The sentence under the title. Every step has one -- a wizard page with a heading and a control
/// and nothing else makes the reader guess what the control is for.
/// </param>
public sealed record OnboardingPage(OnboardingStep Step, string Title, string Summary);
