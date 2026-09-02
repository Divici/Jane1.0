using Jane.App.Controls;
using Jane.Core.Vocabulary;

namespace Jane.App.Settings;

/// <summary>
/// The Deep Context blocklist, shown as what it is: three layers, and a way to ask what any given
/// window would do.
/// </summary>
/// <remarks>
/// <para>
/// The lists themselves are fixed in the source, and deliberately. Deep Context puts on-screen
/// text into a language-model prompt, and the difference between a false positive and a false
/// negative is one lost hint against somebody's account number -- so the built-in entries are not
/// removable from a settings window. What the window owes in exchange is complete visibility:
/// every process, every title fragment, the browser-only list, and the reason there are three
/// lists rather than one.
/// </para>
/// <para>
/// The probe is the part that makes the surface useful rather than merely honest. Typing a
/// process name and a window title and being told, in words, whether Deep Context would read it
/// is the only way to answer "is my bank tab safe" without opening the bank tab.
/// </para>
/// <para>
/// A live <see cref="Blocklist"/> also carries the session's auto-blocks -- processes whose UI
/// Automation provider wedged and which are therefore off until Jane restarts. Those are the one
/// part of this surface that changes while the window is open, and they are the part a user is
/// most likely to be confused by, so they are listed with the reason.
/// </para>
/// </remarks>
public sealed class BlocklistInspector : ObservableObject
{
    private readonly Blocklist _blocklist;
    private string _probeProcess = string.Empty;
    private string _probeTitle = string.Empty;
    private BlockDecision? _probeResult;
    private string _probeMessage = string.Empty;
    private string _filter = string.Empty;

    public BlocklistInspector(Blocklist blocklist) => _blocklist = blocklist;

    /// <summary>Processes that are never read, whatever window they show.</summary>
    public IReadOnlyList<string> Processes =>
        Filtered(Blocklist.DefaultProcesses);

    /// <summary>Title fragments that block in every application.</summary>
    public IReadOnlyList<string> TitleFragments =>
        Filtered(Blocklist.DefaultTitleFragments);

    /// <summary>Finance, health and identity words, which only block inside a browser.</summary>
    public IReadOnlyList<string> BrowserTitleFragments =>
        Filtered(Blocklist.BrowserTitleFragments);

    public IReadOnlyList<string> BrowserProcesses => Filtered(Blocklist.BrowserProcesses);

    /// <summary>Processes auto-blocked for this session because their provider wedged, with why.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AutoBlocked =>
        [.. _blocklist.AutoBlockReasons.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)];

    public bool HasAutoBlocked => _blocklist.AutoBlockReasons.Count > 0;

    /// <summary>Narrows all four lists at once. The lists are long; scrolling them is not reading them.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                Raise(nameof(Processes));
                Raise(nameof(TitleFragments));
                Raise(nameof(BrowserTitleFragments));
                Raise(nameof(BrowserProcesses));
                Raise(nameof(NothingMatches));
            }
        }
    }

    public bool NothingMatches =>
        Processes.Count == 0 && TitleFragments.Count == 0 &&
        BrowserTitleFragments.Count == 0 && BrowserProcesses.Count == 0;

    /// <summary>Process name for the probe, as the focus tracker reports it: lower case, no extension.</summary>
    public string ProbeProcess
    {
        get => _probeProcess;
        set => Set(ref _probeProcess, value);
    }

    public string ProbeTitle
    {
        get => _probeTitle;
        set => Set(ref _probeTitle, value);
    }

    public BlockDecision? ProbeResult
    {
        get => _probeResult;
        private set
        {
            if (Set(ref _probeResult, value))
            {
                Raise(nameof(ProbeSeverity));
            }
        }
    }

    /// <summary>The verdict in words. Empty until the probe has been run at least once.</summary>
    public string ProbeMessage
    {
        get => _probeMessage;
        private set => Set(ref _probeMessage, value);
    }

    public Severity ProbeSeverity => ProbeResult switch
    {
        null => Severity.Info,
        { IsBlocked: true } => Severity.Warning,
        _ => Severity.Success,
    };

    /// <summary>Answers what Deep Context would do with the named window, right now.</summary>
    public void Probe()
    {
        var decision = _blocklist.Evaluate(Normalise(ProbeProcess), ProbeTitle);
        ProbeResult = decision;
        ProbeMessage = Explain(decision);
    }

    /// <summary>Turns a decision into the sentence the window shows.</summary>
    public static string Explain(BlockDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.Reason switch
        {
            BlockReason.BlockedProcess =>
                $"This window would not be read. \"{decision.Match}\" is on the never-read process list.",
            BlockReason.BlockedWindowTitle =>
                $"This window would not be read. Its title contains \"{decision.Match}\".",
            BlockReason.UnknownProcess =>
                "This window would not be read. Jane could not name the process, and an unidentifiable window cannot be cleared against any list.",
            BlockReason.AutoBlockedWedged =>
                $"This window would not be read. Its UI Automation provider wedged earlier in this session ({decision.Match}), so it is off until Jane restarts.",
            BlockReason.BlockedContent =>
                $"The text read back would be discarded: it contains \"{decision.Match}\".",
            _ =>
                "Deep Context would read this window. Anything it reads is stored in history in plaintext until you delete it.",
        };
    }

    /// <summary>
    /// Whether text that came back from a read would survive the content check.
    /// </summary>
    /// <remarks>
    /// The second half of the probe, and the one the title cannot answer: a page called
    /// "Dashboard" served from <c>secure.chase.com</c> passes every pre-read layer.
    /// </remarks>
    public BlockDecision ProbeContent(string text) => _blocklist.EvaluateContent(text);

    private IReadOnlyList<string> Filtered(IReadOnlyList<string> source) =>
        string.IsNullOrWhiteSpace(Filter)
            ? source
            : [.. source.Where(v => v.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>Folds a typed name into the form the focus tracker reports.</summary>
    private static string Normalise(string process)
    {
        var trimmed = process.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToLowerInvariant();
    }
}
