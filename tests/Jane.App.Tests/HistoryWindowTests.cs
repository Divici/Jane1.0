using Jane.App.Controls;
using Jane.App.History;
using Jane.Core.Abstractions;
using Jane.Core.History;

namespace Jane.App.Tests;

/// <summary>
/// History is the most sensitive thing Jane keeps: everything ever dictated, plus whatever Deep
/// Context read off the screen, in plaintext, until somebody deletes it. These tests assert that
/// the window says so, that search and delete do what they claim, and that a re-inject aimed at a
/// window that has gone injects nothing at all.
/// </summary>
public sealed class HistoryWindowTests
{
    private static readonly TargetWindow Editor = new(0x1234, 4242, "code", "Chrome_WidgetWin_1", "plan.md - Jane");
    private static readonly TargetWindow Browser = new(0x5678, 999, "chrome", "Chrome_WidgetWin_1", "Jane - GitHub");

    [Fact]
    public async Task SearchReturnsOnlyTheMatchingRows()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("Let us ship the parser today.", Editor),
            ("The build is green again.", Editor),
            ("Remember to renew the certificate.", Browser));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal(3, window.Model.Rows.Count);

            window.Model.Search = "build";
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var row = Assert.Single(window.Model.Rows);
            Assert.Contains("build is green", row.Text, StringComparison.Ordinal);
            Assert.True(window.Model.IsFiltered);

            window.Model.Search = "nothing matches this";
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Empty(window.Model.Rows);
            Assert.True(window.Model.IsEmpty);
        });
    }

    [Fact]
    public async Task SearchIsLiteralSoAPercentSignFindsAPercentSign()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("Coverage is at 100% on that module.", Editor),
            ("Nothing to report.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            window.Model.Search = "100%";
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var row = Assert.Single(window.Model.Rows);
            Assert.Contains("100%", row.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FilteringByApplicationNarrowsToThatApplication()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("One in the editor.", Editor),
            ("Two in the editor.", Editor),
            ("One in the browser.", Browser));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Contains("chrome", window.Model.Applications);

            window.Model.Application = "chrome";
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var row = Assert.Single(window.Model.Rows);
            Assert.Equal("chrome", row.Application);
        });
    }

    [Fact]
    public async Task DeleteAllEmptiesTheDatabase()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("One.", Editor),
            ("Two.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, window.Model.Rows.Count);

            await window.Model.DeleteEverythingAsync(TestContext.Current.CancellationToken);

            Assert.Empty(window.Model.Rows);
            Assert.True(window.Model.IsEmpty);
            Assert.False(window.Model.IsFiltered);
        });

        Assert.Equal(0, await jane.History.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingOneRowRemovesOnlyThatRow()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("Keep this one.", Editor),
            ("Delete this one.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var doomed = window.Model.Rows.Single(r => r.Text.StartsWith("Delete", StringComparison.Ordinal));
            await window.Model.DeleteAsync(doomed, TestContext.Current.CancellationToken);

            var survivor = Assert.Single(window.Model.Rows);
            Assert.StartsWith("Keep", survivor.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ReinjectIntoADeadWindowToastsAndInjectsNothing()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("Something worth typing again.", Editor));

        var injected = new List<HistoryEntry>();

        await sta.InvokeAsync(async () =>
        {
            // Nothing has focus: the recorded window is gone.
            var window = jane.OpenHistory(live: () => TargetWindow.None, inject: injected.Add);
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            window.Model.Reinject(window.Model.Rows[0]);

            Assert.Empty(injected);

            var toast = Assert.Single(window.Toasts.History);
            Assert.Equal(Severity.Warning, toast.Severity);
            Assert.Contains("no longer", toast.Announcement, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ReinjectIntoADifferentWindowToastsAndInjectsNothing()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("Something worth typing again.", Editor));

        var injected = new List<HistoryEntry>();

        await sta.InvokeAsync(async () =>
        {
            // Something else has focus now, which includes the recycled-handle case.
            var window = jane.OpenHistory(live: () => Browser, inject: injected.Add);
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            window.Model.Reinject(window.Model.Rows[0]);

            Assert.Empty(injected);

            var toast = Assert.Single(window.Toasts.History);
            Assert.Contains("chrome", toast.Announcement, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("code", toast.Announcement, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ReinjectWithNoRecordedTargetToastsAndInjectsNothing()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("An aborted dictation.", TargetWindow.None));

        var injected = new List<HistoryEntry>();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory(live: () => Editor, inject: injected.Add);
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            window.Model.Reinject(window.Model.Rows[0]);

            Assert.Empty(injected);
            Assert.Contains("no window recorded", Assert.Single(window.Toasts.History).Announcement, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ReinjectIntoTheRecordedWindowInjectsExactlyThatEntry()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("Type this again, please.", Editor));

        var injected = new List<HistoryEntry>();

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory(live: () => Editor, inject: injected.Add);
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            window.Model.Reinject(window.Model.Rows[0]);

            var entry = Assert.Single(injected);
            Assert.Equal("Type this again, please.", entry.FinalText);
        });
    }

    [Fact]
    public async Task CopyPutsTheFinalTextWhereTheCallerAsked()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("Copy me.", Editor));

        string? copied = null;

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory(copy: text => copied = text);
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            window.Model.Copy(window.Model.Rows[0]);
        });

        Assert.Equal("Copy me.", copied);
    }

    [Fact]
    public void TheWindowStatesPlainlyWhatItKeeps()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenHistory();
            UiTree.Realize(window.Page);

            var text = UiTree.AllText(window.Page);

            // The plan requires this said in the window, not in a tooltip and not in a document.
            Assert.Contains("plaintext", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("until you delete", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Deep Context", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Audio is never", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AnEmptyHistoryShowsADesignedEmptyStateRatherThanABlankPane()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        sta.Invoke(() =>
        {
            var window = jane.OpenHistory();

            Assert.True(window.Model.IsEmpty);
            Assert.False(window.Model.IsFiltered);
            Assert.False(string.IsNullOrWhiteSpace(window.Model.EmptyTitle));
            Assert.False(string.IsNullOrWhiteSpace(window.Model.EmptyDescription));

            UiTree.Realize(window.Page);
            Assert.Contains(window.Model.EmptyTitle, UiTree.AllText(window.Page), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AFailedReadShowsAnErrorStateRatherThanAnEmptyList()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("This will not be readable.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();

            // Closing the database under the window is the cheapest honest way to make the read
            // fail: the store is concrete, and a fake would be asserting against a fake.
            jane.Database.Dispose();

            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(window.Model.Error);
            Assert.False(window.Model.IsLoading);
            Assert.Empty(window.Model.Rows);
        });
    }

    [Fact]
    public async Task ARowShowsWhenAndWhereAlongsideWhat()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(("The quarterly numbers are in.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var row = window.Model.Rows[0];

            Assert.Equal("code", row.Application);
            Assert.Contains("plan.md", row.WindowTitle, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(row.When));
            Assert.False(string.IsNullOrWhiteSpace(row.Relative));
            Assert.False(string.IsNullOrWhiteSpace(row.TimingSummary));
            Assert.False(string.IsNullOrWhiteSpace(row.ModeLabel));
            Assert.False(string.IsNullOrWhiteSpace(row.FormattingLabel));
        });
    }

    [Fact]
    public async Task ARowThatCarriesDeepContextSaysSo()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.History.AppendAsync(
            new HistoryEntry
            {
                RawTranscript = "with the context",
                FinalText = "With the context.",
                Target = Editor,
                DeepContext = "Kubernetes cluster overview - staging",
                EngineId = "parakeet-tdt-0.6b-v2-int8",
            },
            TestContext.Current.CancellationToken);

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            var row = window.Model.Rows[0];
            Assert.True(row.HasDeepContext);
            Assert.Contains("Kubernetes", row.DeepContext!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EveryInteractiveControlHasAnAccessibleNameAndATabStop()
    {
        using var sta = new StaTestContext();
        using var jane = new TempJane();

        await jane.SeedHistoryAsync(
            ("A row, so the per-row controls exist to be checked.", Editor));

        await sta.InvokeAsync(async () =>
        {
            var window = jane.OpenHistory();
            await window.Model.RefreshAsync(TestContext.Current.CancellationToken);

            UiTree.Realize(window.Page);

            var named = UiTree.AssertEveryInteractiveControlIsNamed(window.Page, "history window");
            UiTree.AssertEveryInteractiveControlIsATabStop(window.Page, "history window");

            Assert.True(named > 5, $"Only {named} interactive controls were found -- the walk is not reaching the list.");
        });
    }
}
