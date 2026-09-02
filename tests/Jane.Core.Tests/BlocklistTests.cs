using Jane.Core.Vocabulary;

namespace Jane.Core.Tests;

/// <summary>
/// The rules that decide whether Deep Context is allowed to look at a window at all.
/// </summary>
/// <remarks>
/// This is the phase's privacy boundary, so it is tested as policy in <c>Jane.Core</c> with plain
/// strings rather than through UI Automation. Every rule here runs <em>before</em> any UIA call is
/// made, which is what makes "a blocklisted process makes no UIA call" an assertion about the
/// design rather than about timing.
/// </remarks>
public sealed class BlocklistTests
{
    [Fact]
    public void APasswordManagerIsBlockedByProcessName()
    {
        var decision = new Blocklist().Evaluate("1password", "1Password");

        Assert.True(decision.IsBlocked);
        Assert.Equal(BlockReason.BlockedProcess, decision.Reason);
        Assert.Equal("1password", decision.Match);
    }

    [Fact]
    public void AnOrdinaryEditorIsNotBlocked()
    {
        var decision = new Blocklist().Evaluate("code", "UiaWorker.cs - jane1.0 - Visual Studio Code");

        Assert.False(decision.IsBlocked);
        Assert.Equal(BlockReason.None, decision.Reason);
    }

    [Fact]
    public void ABrowserOnABankingTitleIsBlockedEvenThoughTheProcessIsAllowed()
    {
        // The whole reason a process-name list is not enough: every website is chrome.exe.
        var blocklist = new Blocklist();

        Assert.False(blocklist.Evaluate("chrome", "Phase 8 plan - Google Docs - Google Chrome").IsBlocked);

        var banking = blocklist.Evaluate("chrome", "Chase Online - Account Summary - Google Chrome");
        Assert.True(banking.IsBlocked);
        Assert.Equal(BlockReason.BlockedWindowTitle, banking.Reason);
    }

    [Fact]
    public void EveryShippedBrowserGetsTheTitleHeuristics()
    {
        var blocklist = new Blocklist();

        foreach (var browser in Blocklist.BrowserProcesses)
        {
            Assert.True(
                blocklist.Evaluate(browser, "Barclays Online Banking").IsBlocked,
                $"{browser} is listed as a browser but did not get the title heuristics.");
        }
    }

    [Fact]
    public void TheFinanceHeuristicsAreBrowserOnlySoAnEditorCanStillOpenAFileCalledBank()
    {
        // "bank" in a code editor is a variable name, not a bank. Applying the finance list
        // everywhere would silently disable Deep Context for anyone working on payments code.
        var blocklist = new Blocklist();

        Assert.False(blocklist.Evaluate("code", "bank-transfer.ts - payments - Visual Studio Code").IsBlocked);
        Assert.True(blocklist.Evaluate("chrome", "bank transfer - Chase").IsBlocked);
    }

    [Fact]
    public void APasswordTitleIsBlockedInEveryApplication()
    {
        // Unlike the finance list, an explicit credential surface is unambiguous wherever it
        // appears -- a terminal prompting for a passphrase is exactly as sensitive as a web form.
        var blocklist = new Blocklist();

        Assert.True(blocklist.Evaluate("windowsterminal", "Enter passphrase for key - Windows Terminal").IsBlocked);
        Assert.True(blocklist.Evaluate("chrome", "Sign in - Google Accounts").IsBlocked);
        Assert.True(blocklist.Evaluate("someapp", "Recovery phrase").IsBlocked);
    }

    [Fact]
    public void AWindowWhoseProcessCannotBeNamedIsBlocked()
    {
        // FocusedAppIdentity degrades an unreadable process name to empty rather than throwing.
        // An unidentifiable window cannot be cleared against any list, so it is never read.
        var decision = new Blocklist().Evaluate(string.Empty, "Something");

        Assert.True(decision.IsBlocked);
        Assert.Equal(BlockReason.UnknownProcess, decision.Reason);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var blocklist = new Blocklist();

        Assert.True(blocklist.Evaluate("BitWarden", "Vault").IsBlocked);
        Assert.True(blocklist.Evaluate("CHROME", "WELLS FARGO - ONLINE BANKING").IsBlocked);
    }

    [Fact]
    public void ContentThatNamesABankingHostIsBlockedAfterTheRead()
    {
        // The second layer. A browser title is often just the page title, so the URL only becomes
        // visible once the address bar or the document has actually been read; a hit there
        // discards the whole read rather than trying to redact part of it.
        var blocklist = new Blocklist();

        var hit = blocklist.EvaluateContent("https://secure.chase.com/web/auth/dashboard#/dashboard/index");
        Assert.True(hit.IsBlocked);
        Assert.Equal(BlockReason.BlockedContent, hit.Reason);

        Assert.False(blocklist.EvaluateContent("https://kubernetes.io/docs/concepts/").IsBlocked);
    }

    [Fact]
    public void ContentCarryingACredentialLabelIsBlocked()
    {
        var blocklist = new Blocklist();

        Assert.True(blocklist.EvaluateContent("Account number: 4111111111111111").IsBlocked);
        Assert.True(blocklist.EvaluateContent("api_key = sk-live-9f2b").IsBlocked);
    }

    [Fact]
    public void AutoBlockingAProcessBlocksItForTheRestOfTheSessionAndRecordsWhy()
    {
        var blocklist = new Blocklist();
        Assert.False(blocklist.Evaluate("slowapp", "Some document").IsBlocked);

        Assert.True(blocklist.AutoBlockForSession("SlowApp", "the UIA read did not return"));

        var decision = blocklist.Evaluate("slowapp", "Some document");
        Assert.True(decision.IsBlocked);
        Assert.Equal(BlockReason.AutoBlockedWedged, decision.Reason);
        Assert.Contains("slowapp", blocklist.AutoBlocked);
        Assert.Contains("did not return", blocklist.AutoBlockReasons["slowapp"], StringComparison.Ordinal);
    }

    [Fact]
    public void AutoBlockingTheSameProcessTwiceIsIdempotent()
    {
        var blocklist = new Blocklist();

        Assert.True(blocklist.AutoBlockForSession("slowapp", "first"));
        Assert.False(blocklist.AutoBlockForSession("SLOWAPP", "second"));
        Assert.Single(blocklist.AutoBlocked);

        // The first reason is kept: it is the one that actually describes the wedge.
        Assert.Equal("first", blocklist.AutoBlockReasons["slowapp"]);
    }

    [Fact]
    public void AutoBlockingAnUnnamedProcessIsRefusedRatherThanBlockingEverything()
    {
        // A wedge on a window Jane could not name must not turn into a blanket block keyed on the
        // empty string -- Evaluate already refuses unnamed windows, and an empty entry here would
        // be indistinguishable from "the whole session is blocklisted".
        var blocklist = new Blocklist();

        Assert.False(blocklist.AutoBlockForSession("   ", "wedged"));
        Assert.Empty(blocklist.AutoBlocked);
    }

    [Fact]
    public void ExtraEntriesAddToTheDefaultsRatherThanReplacingThem()
    {
        var blocklist = new Blocklist(
            extraProcesses: ["ledgerapp"],
            extraTitleFragments: ["quarterly results"]);

        Assert.True(blocklist.Evaluate("ledgerapp", "Ledger").IsBlocked);
        Assert.True(blocklist.Evaluate("notepad", "Quarterly Results draft").IsBlocked);
        Assert.True(blocklist.Evaluate("1password", "Vault").IsBlocked);
    }

    [Fact]
    public void DefaultsAreLowerCaseAndDeduplicated()
    {
        // The lists are compared with ordinal case-insensitive matching against a lower-cased
        // process name, so a stray upper-case entry would silently never match.
        AssertNormalised(Blocklist.DefaultProcesses);
        AssertNormalised(Blocklist.DefaultTitleFragments);
        AssertNormalised(Blocklist.BrowserTitleFragments);
        AssertNormalised(Blocklist.BrowserProcesses);

        static void AssertNormalised(IReadOnlyList<string> entries)
        {
            Assert.All(entries, entry => Assert.Equal(entry.ToLowerInvariant(), entry));
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry)));
            Assert.Equal(entries.Count, entries.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
