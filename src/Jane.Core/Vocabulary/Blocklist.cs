using System.Text.RegularExpressions;

namespace Jane.Core.Vocabulary;

/// <summary>Why Deep Context refused to look at a window.</summary>
public enum BlockReason
{
    None,

    /// <summary>The process name is on the list -- a password manager, a credential prompt.</summary>
    BlockedProcess,

    /// <summary>The window title matched a sensitive fragment. The only signal a browser gives cheaply.</summary>
    BlockedWindowTitle,

    /// <summary>
    /// The window could not be named. An unidentifiable window cannot be cleared against any
    /// list, so it is never read.
    /// </summary>
    UnknownProcess,

    /// <summary>Text that came back from a read named a sensitive host or carried a secret.</summary>
    BlockedContent,

    /// <summary>The UIA worker wedged on this process, so it is off for the rest of the session.</summary>
    AutoBlockedWedged,
}

/// <param name="Match">The entry that matched, for the settings surface and the log.</param>
public sealed record BlockDecision(bool IsBlocked, BlockReason Reason, string? Match)
{
    public static BlockDecision Allowed { get; } = new(false, BlockReason.None, null);
}

/// <summary>
/// Decides whether Deep Context may read a given window, and what it must throw away afterwards.
/// </summary>
/// <remarks>
/// <para>
/// A process-name list on its own cannot protect web banking: every site in the world is
/// <c>chrome.exe</c>. So there are three layers, and every one of them runs before or after the
/// UI Automation call rather than inside it.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Process name.</b> Password managers, the Windows credential broker, the UAC consent host.
/// These are never read whatever their title says.
/// </description></item>
/// <item><description>
/// <b>Window title.</b> Two lists. <see cref="DefaultTitleFragments"/> holds unambiguous
/// credential surfaces and applies to every application -- a terminal asking for a passphrase is
/// exactly as sensitive as a web form. <see cref="BrowserTitleFragments"/> holds finance, health
/// and identity words and applies <em>only</em> to browsers, because "bank" in a code editor is a
/// variable name and blocking it would quietly disable Deep Context for anyone working on
/// payments code.
/// </description></item>
/// <item><description>
/// <b>Content, after the read.</b> A page title is often just the page title, so the URL only
/// becomes visible once the address bar or the document has been read. A hit here discards the
/// whole read rather than trying to redact part of it.
/// </description></item>
/// </list>
/// <para>
/// The lists are deliberately generous. A false positive costs a little accuracy on one
/// dictation -- Jane still transcribes, just without hints. A false negative puts somebody's
/// account number into a language-model prompt. Those are not comparable, so this errs toward
/// refusing.
/// </para>
/// <para>
/// Instances are safe to share across threads: the fixed lists are immutable and the
/// session-scoped auto-block set is guarded by a lock, because it is written from the UIA
/// worker's supervisor and read from whichever thread handled the key-down.
/// </para>
/// </remarks>
public sealed partial class Blocklist
{
    private readonly HashSet<string> _processes;
    private readonly List<string> _titleFragments;
    private readonly Dictionary<string, string> _autoBlocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="extraProcesses">Added to the defaults, never replacing them.</param>
    /// <param name="extraTitleFragments">Added to the always-on title list.</param>
    public Blocklist(
        IEnumerable<string>? extraProcesses = null,
        IEnumerable<string>? extraTitleFragments = null)
    {
        _processes = new HashSet<string>(DefaultProcesses, StringComparer.OrdinalIgnoreCase);
        _titleFragments = [.. DefaultTitleFragments];

        foreach (var process in extraProcesses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(process))
            {
                _processes.Add(process.Trim());
            }
        }

        foreach (var fragment in extraTitleFragments ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                _titleFragments.Add(fragment.Trim().ToLowerInvariant());
            }
        }
    }

    /// <summary>Processes that are never read, whatever window they show.</summary>
    public static IReadOnlyList<string> DefaultProcesses { get; } =
    [
        // Password managers and vaults.
        "1password", "1password-cli", "bitwarden", "keepass", "keepass2", "keepassxc",
        "lastpass", "dashlane", "enpass", "keeper", "nordpass", "roboform", "padloc",
        "authy", "winauth", "protonpass",

        // Windows' own credential surfaces.
        "credentialuibroker", "consent", "logonui", "lockapp", "keymgr", "cngcredui",
        "credwiz",

        // Remote sessions, where the pixels belong to somebody else's desktop entirely.
        "mstsc", "vmconnect",
    ];

    /// <summary>
    /// Title fragments that block in <em>any</em> application. Unambiguous credential surfaces.
    /// </summary>
    public static IReadOnlyList<string> DefaultTitleFragments { get; } =
    [
        "password", "passphrase", "passcode", "credential", "sign in", "signin", "sign-in",
        "log in", "login", "log on", "logon", "authentication", "authenticator",
        "two-factor", "two factor", "2fa", "one-time", "one time code", "verification code",
        "security code", "private key", "secret key", "api key", "recovery phrase",
        "seed phrase", "mnemonic", "unlock vault", "master key", "keychain", "wallet",
    ];

    /// <summary>
    /// Title fragments that block only in a browser. Finance, health and identity words, which
    /// are ordinary vocabulary everywhere else.
    /// </summary>
    public static IReadOnlyList<string> BrowserTitleFragments { get; } =
    [
        "bank", "banking", "checking account", "savings account", "account summary",
        "credit card", "debit card", "card number", "sort code", "routing number",
        "billing", "payment", "checkout", "payout", "transfer money", "wire transfer",
        "brokerage", "portfolio", "crypto", "exchange login",
        "chase", "wells fargo", "citibank", "capital one", "barclays", "hsbc", "lloyds",
        "natwest", "santander", "monzo", "starling", "revolut", "paypal", "venmo", "wise.com",
        "coinbase", "binance", "kraken", "robinhood", "fidelity", "vanguard", "schwab",
        "e-trade", "etrade", "ameritrade",
        "irs.gov", "tax return", "hmrc", "self assessment",
        "medical record", "patient portal", "mychart", "health record", "insurance claim",
        "social security", "passport", "driver's licence", "driver's license",
    ];

    /// <summary>
    /// Processes whose window title is a web page title rather than a document name.
    /// </summary>
    /// <remarks>
    /// Electron shells (Slack, VS Code, Discord) are deliberately absent: they render web content
    /// but their titles are workspace and file names, so the finance list would produce constant
    /// false positives with no matching risk.
    /// </remarks>
    public static IReadOnlyList<string> BrowserProcesses { get; } =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium",
        "arc", "iexplore", "safari", "librewolf", "waterfox", "zen", "thorium",
    ];

    /// <summary>Process names auto-blocked for this session because their provider wedged.</summary>
    public IReadOnlyCollection<string> AutoBlocked
    {
        get
        {
            lock (_gate)
            {
                return [.. _autoBlocked.Keys];
            }
        }
    }

    /// <summary>Why each auto-blocked process was blocked. Surfaced in settings.</summary>
    public IReadOnlyDictionary<string, string> AutoBlockReasons
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_autoBlocked, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The pre-read decision. Runs entirely on strings the focus tracker already has, so a
    /// blocked window costs no UI Automation call at all.
    /// </summary>
    /// <param name="processName">Lower-case, extension stripped, as <c>TargetWindow</c> carries it.</param>
    public BlockDecision Evaluate(string? processName, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return new BlockDecision(true, BlockReason.UnknownProcess, null);
        }

        var process = processName.Trim();

        lock (_gate)
        {
            if (_autoBlocked.TryGetValue(process, out var reason))
            {
                return new BlockDecision(true, BlockReason.AutoBlockedWedged, reason);
            }
        }

        if (_processes.Contains(process))
        {
            return new BlockDecision(true, BlockReason.BlockedProcess, process.ToLowerInvariant());
        }

        var title = (windowTitle ?? string.Empty).ToLowerInvariant();
        if (title.Length > 0)
        {
            foreach (var fragment in _titleFragments)
            {
                if (title.Contains(fragment, StringComparison.Ordinal))
                {
                    return new BlockDecision(true, BlockReason.BlockedWindowTitle, fragment);
                }
            }

            if (IsBrowser(process))
            {
                foreach (var fragment in BrowserTitleFragments)
                {
                    if (title.Contains(fragment, StringComparison.Ordinal))
                    {
                        return new BlockDecision(true, BlockReason.BlockedWindowTitle, fragment);
                    }
                }
            }
        }

        return BlockDecision.Allowed;
    }

    /// <summary>Whether the finance and identity title heuristics apply to this process.</summary>
    public static bool IsBrowser(string? processName) =>
        processName is not null &&
        BrowserProcesses.Contains(processName.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The post-read decision, applied to everything a read returned before any of it is kept.
    /// </summary>
    /// <remarks>
    /// This is what catches the case the title cannot: a page called "Dashboard" served from
    /// <c>secure.chase.com</c>, or a form field that happens to hold an account number. It is a
    /// whole-read verdict on purpose -- partial redaction of text that is about to be summarised
    /// by a language model is a guess, and discarding is not.
    /// </remarks>
    public BlockDecision EvaluateContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BlockDecision.Allowed;
        }

        var lowered = text.ToLowerInvariant();

        foreach (var fragment in BrowserTitleFragments)
        {
            // Host fragments only. A prose mention of "payment" in an email is not a reason to
            // throw the read away, but "chase.com" or "coinbase" in a URL is.
            if (fragment.Contains('.') && lowered.Contains(fragment, StringComparison.Ordinal))
            {
                return new BlockDecision(true, BlockReason.BlockedContent, fragment);
            }
        }

        foreach (var host in SensitiveHostFragments)
        {
            if (lowered.Contains(host, StringComparison.Ordinal))
            {
                return new BlockDecision(true, BlockReason.BlockedContent, host);
            }
        }

        foreach (var label in CredentialLabels)
        {
            if (lowered.Contains(label, StringComparison.Ordinal))
            {
                return new BlockDecision(true, BlockReason.BlockedContent, label);
            }
        }

        try
        {
            if (SecretShapedToken().IsMatch(text))
            {
                return new BlockDecision(true, BlockReason.BlockedContent, "secret-shaped token");
            }

            if (LongDigitRun().IsMatch(text))
            {
                return new BlockDecision(true, BlockReason.BlockedContent, "long digit run");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Text pathological enough to time out the scan is text this cannot vouch for, and
            // the safe answer to "is there a secret in here" is always yes.
            return new BlockDecision(true, BlockReason.BlockedContent, "content could not be scanned in time");
        }

        return BlockDecision.Allowed;
    }

    /// <summary>
    /// Turns a process off for the rest of the session after its UIA provider wedged.
    /// </summary>
    /// <returns>False when the name is unusable or the process was already blocked.</returns>
    /// <remarks>
    /// Refusing an empty name matters: <see cref="Evaluate"/> already blocks unnamed windows, and
    /// an entry keyed on the empty string would be indistinguishable from "the whole session is
    /// blocklisted" in the settings surface.
    /// </remarks>
    public bool AutoBlockForSession(string? processName, string reason)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        lock (_gate)
        {
            // Stored lower-cased, matching how TargetWindow reports a process name, so the set
            // the settings screen renders reads the same however the wedge was noticed. The first
            // reason is kept -- it is the one that actually describes the wedge.
            return _autoBlocked.TryAdd(processName.Trim().ToLowerInvariant(), reason);
        }
    }

    /// <summary>Host substrings that appear in URLs but rarely in prose.</summary>
    private static readonly string[] SensitiveHostFragments =
    [
        "chase.com", "bankofamerica.com", "wellsfargo.com", "citi.com", "capitalone.com",
        "barclays.co.uk", "hsbc.co", "lloydsbank.com", "natwest.com", "santander.co",
        "monzo.com", "starlingbank.com", "revolut.com", "paypal.com", "venmo.com",
        "coinbase.com", "binance.com", "kraken.com", "robinhood.com", "fidelity.com",
        "vanguard.com", "schwab.com", "irs.gov", "hmrc.gov.uk", "ssa.gov",
        "/login", "/signin", "/sign-in", "/auth/", "/onlinebanking", "/account/security",
    ];

    /// <summary>Labels that mean the next token is a secret, wherever they appear.</summary>
    private static readonly string[] CredentialLabels =
    [
        "account number", "card number", "cvv", "cvc", "sort code", "routing number",
        "iban", "swift code", "api key", "api_key", "apikey", "access token", "access_token",
        "client secret", "client_secret", "private key", "secret key", "bearer ",
        "authorization:", "password:", "passphrase:", "pin:", "otp:", "seed phrase",
        "social security number", "ssn:",
    ];

    /// <summary>
    /// Vendor-prefixed keys and long opaque strings. Matching the shape rather than a vendor list
    /// is what makes this hold up for a provider nobody thought of.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:sk|pk|rk)[-_](?:live|test|prod)?[-_]?[A-Za-z0-9]{8,}|\bgh[pousr]_[A-Za-z0-9]{16,}|\bxox[baprs]-[A-Za-z0-9-]{10,}|\bAKIA[0-9A-Z]{12,}|\beyJ[A-Za-z0-9_-]{20,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex SecretShapedToken();

    /// <summary>
    /// A card or account number: twelve or more contiguous digits, or the classic four groups of
    /// four. Deliberately not "twelve digits with any separators between them", which would match
    /// every log timestamp on screen and turn Deep Context off for anyone reading a build log.
    /// </summary>
    [GeneratedRegex(
        @"\b\d{12,}\b|\b(?:\d{4}[ -]){3}\d{4}\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex LongDigitRun();
}
