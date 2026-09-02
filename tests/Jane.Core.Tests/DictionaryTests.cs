using Jane.Core.Abstractions;
using Jane.Core.Instructions;
using Jane.Core.Platform;
using Jane.Core.Storage;
using Jane.Core.Vocabulary;

namespace Jane.Core.Tests;

/// <summary>
/// The custom dictionary: what it feeds sherpa-onnx, what it feeds the prompt, and when it takes
/// the Phase 7 bypass away.
/// </summary>
public sealed class DictionaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("jane-dictionary-test");
    private readonly JaneDatabase _database;
    private readonly UserDictionary _dictionary;

    public DictionaryTests()
    {
        _database = JaneDatabase.Open(new JanePaths(_root.FullName, Path.Combine(_root.FullName, "models")));
        _dictionary = new UserDictionary(_database);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DictionaryTermIsSurfacedAsAHotword()
    {
        // P1-6 measured contextual biasing at 88% of greedy's p50, so biasing is enabled and the
        // dictionary really does reach the decoder rather than only the prompt.
        await _dictionary.AddAsync("Kubernetes", null, null, Token);

        Assert.Contains("Kubernetes", _dictionary.Hotwords);

        var options = new RecognitionOptions(_dictionary.Hotwords);
        Assert.True(options.HasHotwords);
    }

    [Fact]
    public async Task PronunciationHintIsAlsoAHotword_BecauseThatIsWhatTheRecogniserActuallyHears()
    {
        await _dictionary.AddAsync("Kubernetes", "koober netties", null, Token);

        Assert.Contains("Kubernetes", _dictionary.Hotwords);
        Assert.Contains("koober netties", _dictionary.Hotwords);
    }

    [Fact]
    public async Task PendingReplacementSuppressesTheBypass()
    {
        // BLOCKER #9: the bypass silently skipping the user's own dictionary is the failure this
        // guards. If a replacement applies, the LLM has to run.
        await _dictionary.AddAsync("k eight s", null, "k8s", Token);

        Assert.True(_dictionary.HasPendingReplacement("deploy this to k eight s tomorrow"));

        var pending = _dictionary.PendingReplacementsFor("deploy this to k eight s tomorrow");
        Assert.Single(pending);
        Assert.Equal("k8s", pending[0].Replacement);
    }

    [Fact]
    public async Task NoPendingReplacement_LeavesTheBypassAlone()
    {
        await _dictionary.AddAsync("k eight s", null, "k8s", Token);
        await _dictionary.AddAsync("Kubernetes", "koober netties", null, Token);

        // A term with no replacement is a biasing hint only -- it changes nothing after the fact,
        // so there is nothing for the LLM to do and the bypass stays available.
        Assert.False(_dictionary.HasPendingReplacement("send it on Wednesday"));
        Assert.False(_dictionary.HasPendingReplacement("deploy this to Kubernetes tomorrow"));
    }

    [Fact]
    public async Task ReplacementMatchesOnTheHintToo_SinceThatIsWhatTheTranscriptWillContain()
    {
        await _dictionary.AddAsync("k8s", "kates", "k8s", Token);

        Assert.True(_dictionary.HasPendingReplacement("roll it out on kates"));
    }

    [Fact]
    public async Task WordBoundariesAreRespected_SoASubstringNeverTriggers()
    {
        await _dictionary.AddAsync("cat", null, "CAT scan", Token);

        Assert.False(_dictionary.HasPendingReplacement("the concatenation failed"));
        Assert.True(_dictionary.HasPendingReplacement("book the cat in for Tuesday"));
    }

    [Fact]
    public async Task ReplacementsApplyDeterministically_ForTheInGameRouteWhereTheLlmIsSkipped()
    {
        // In-game the LLM is skipped entirely, so without a deterministic pass the user's
        // replacements would never happen at all on that route.
        await _dictionary.AddAsync("k eight s", null, "k8s", Token);
        await _dictionary.AddAsync("asap", null, "as soon as possible", Token);

        Assert.Equal(
            "Deploy to k8s as soon as possible.",
            _dictionary.ApplyReplacements("Deploy to k eight s asap."));
    }

    [Fact]
    public async Task DisabledEntryIsNeitherAHotwordNorAReplacement()
    {
        var entry = await _dictionary.AddAsync("Kubernetes", "koober netties", "K8s", Token);
        await _dictionary.SetEnabledAsync(entry.Id, false, Token);

        Assert.DoesNotContain("Kubernetes", _dictionary.Hotwords);
        Assert.False(_dictionary.HasPendingReplacement("deploy this to Kubernetes"));
    }

    [Fact]
    public async Task AddingAnExistingTermEditsItRatherThanDuplicatingIt()
    {
        var first = await _dictionary.AddAsync("Kubernetes", null, null, Token);
        var second = await _dictionary.AddAsync("kubernetes", "koober netties", "K8s", Token);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_dictionary.Entries);
        Assert.Equal("K8s", _dictionary.Entries[0].Replacement);
    }

    [Fact]
    public async Task ThereIsNoEntryCap()
    {
        // Aqua caps the dictionary at 800. That is a product limit, not a technical one, and
        // nothing here imposes it.
        await _dictionary.AddRangeAsync(
            [.. Enumerable.Range(0, 2_500).Select(i => new DictionaryDraft($"term-{i}"))],
            Token);

        Assert.Equal(2_500, _dictionary.Entries.Count);
        Assert.Equal(2_500, _dictionary.Hotwords.Count);
    }

    [Fact]
    public async Task EntriesPersistAcrossRestart()
    {
        await _dictionary.AddAsync("Kubernetes", "koober netties", "K8s", Token);
        _database.Dispose();

        using var reopened = JaneDatabase.Open(new JanePaths(_root.FullName));
        var reloaded = new UserDictionary(reopened);

        Assert.Single(reloaded.Entries);
        Assert.Equal("koober netties", reloaded.Entries[0].PronunciationHint);
        Assert.Equal("K8s", reloaded.Entries[0].Replacement);
    }

    [Fact]
    public async Task RemoveAndClearTakeEntriesOutOfBiasingImmediately()
    {
        var kept = await _dictionary.AddAsync("Kubernetes", null, null, Token);
        var dropped = await _dictionary.AddAsync("Prometheus", null, null, Token);

        await _dictionary.RemoveAsync(dropped.Id, Token);
        Assert.DoesNotContain("Prometheus", _dictionary.Hotwords);
        Assert.Contains("Kubernetes", _dictionary.Hotwords);

        await _dictionary.ClearAsync(Token);
        Assert.Empty(_dictionary.Entries);
        Assert.Empty(_dictionary.Hotwords);
        Assert.NotEqual(0, kept.Id);
    }

    [Fact]
    public async Task PromptSectionCarriesTermsHintsAndReplacements()
    {
        await _dictionary.AddAsync("Kubernetes", "koober netties", null, Token);
        await _dictionary.AddAsync("k eight s", null, "k8s", Token);

        var section = _dictionary.PromptSection();

        Assert.Contains("Kubernetes", section, StringComparison.Ordinal);
        Assert.Contains("koober netties", section, StringComparison.Ordinal);
        Assert.Contains("k8s", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TermsAreForThePromptAndHotwordsAreForTheDecoder()
    {
        // The prompt block says "spell these correctly", so the mangled form must not be in it.
        await _dictionary.AddAsync("Kubernetes", "koober netties", null, Token);

        Assert.Equal(["Kubernetes"], _dictionary.Terms);
        Assert.Equal(["Kubernetes", "koober netties"], _dictionary.Hotwords);
    }

    [Fact]
    public async Task PolicySourceGivesTheBypassBothReasonsToStayOut()
    {
        var instructions = new CustomInstructions(_database);
        await instructions.SetForAppAsync("code", "Terse, no trailing period.", Token);
        await _dictionary.AddAsync("k eight s", null, "k8s", Token);
        await _dictionary.AddAsync("Kubernetes", "koober netties", null, Token);

        var source = new StoredFormattingPolicySource(_dictionary, instructions);
        var policy = source.Resolve("deploy to k eight s", new FormattingContext("code"));

        Assert.Contains("Terse", policy.CustomInstructions!, StringComparison.Ordinal);
        Assert.Single(policy.Replacements);
        Assert.Equal("k eight s", policy.Replacements[0].Spoken);
        Assert.Equal("k8s", policy.Replacements[0].Replacement);
        Assert.Contains("Kubernetes", policy.Terms);
        Assert.DoesNotContain("koober netties", policy.Terms);
    }

    [Fact]
    public async Task PolicySourceReportsTheSpokenFormTheTranscriptActuallyContains()
    {
        await _dictionary.AddAsync("k8s", "kates", "k8s cluster", Token);

        var source = new StoredFormattingPolicySource(_dictionary, new CustomInstructions(_database));
        var policy = source.Resolve("roll it out on kates", new FormattingContext("notepad"));

        Assert.Single(policy.Replacements);
        Assert.Equal("kates", policy.Replacements[0].Spoken);
    }

    [Fact]
    public void PolicySourceOnAnEmptyDatabaseLeavesTheBypassAvailable()
    {
        var source = new StoredFormattingPolicySource(_dictionary, new CustomInstructions(_database));

        var policy = source.Resolve("send it on Wednesday", new FormattingContext("notepad"));

        Assert.Null(policy.CustomInstructions);
        Assert.Empty(policy.Replacements);
        Assert.False(policy.HasCustomInstructions);
    }

    [Fact]
    public void EmptyDictionaryProducesNoPromptSectionAndNoHotwords()
    {
        Assert.Empty(_dictionary.Hotwords);
        Assert.Equal(string.Empty, _dictionary.PromptSection());
        Assert.False(_dictionary.HasPendingReplacement("anything at all"));
    }

    public void Dispose()
    {
        _database.Dispose();
        _root.Delete(recursive: true);
    }
}
