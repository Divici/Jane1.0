using Jane.App.Controls;
using Jane.App.Settings;
using Jane.Core.Models;

namespace Jane.App.Tests;

/// <summary>
/// "Not downloaded" was the wrong answer to the wrong question.
/// </summary>
/// <remarks>
/// <para>
/// The published build ships without <c>tools\ollama</c>, so Jane's supervised model server never
/// started. Every language-model row then asked a socket nobody was listening on, and a failed
/// probe collapsed into <c>IsInstalled = false</c> -- rendered as "Not downloaded", next to a
/// Download button whose only possible outcome was another failure.
/// </para>
/// <para>
/// Three states, not two. A model can be here, absent, or unaskable, and the third is neither of
/// the first two. The host itself becomes an ordinary row in the same list, so the fix for
/// "unavailable" is a button in the place the user is already looking rather than a documented
/// PowerShell script they have no checkout to run.
/// </para>
/// </remarks>
public sealed class ModelHostTests
{
    [Fact]
    public async Task AModelIsInstalledWhenTheHostCanBeAskedAndSaysYes()
    {
        var provisioner = new FakeProvisioner();
        await provisioner.PullAsync(LlmModelCatalog.Gpu.Tag, null, TestContext.Current.CancellationToken);

        var row = ModelRow.ForLlm(LlmModelCatalog.Gpu, provisioner, required: false);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.Installed, row.Availability);
        Assert.True(row.IsInstalled);
        Assert.Equal("Installed", row.StatusText);
        Assert.Equal(Severity.Success, row.StatusSeverity);
    }

    [Fact]
    public async Task AModelIsNotDownloadedWhenTheHostCanBeAskedAndSaysNo()
    {
        var row = ModelRow.ForLlm(LlmModelCatalog.Gpu, new FakeProvisioner(), required: false);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.NotDownloaded, row.Availability);
        Assert.Equal("Not downloaded", row.StatusText);
        Assert.True(row.Download.CanExecute(null));
    }

    [Fact]
    public async Task AModelIsUnavailableRatherThanAbsentWhenThereIsNoHostToAsk()
    {
        // The reported bug, stated as an assertion.
        var provisioner = new FakeProvisioner
        {
            Host = ModelHostState.Missing(
                "The model runtime (ollama.exe) could not be found.",
                "Download it with the Model runtime row above."),
        };

        var row = ModelRow.ForLlm(LlmModelCatalog.Gpu, provisioner, required: false);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.Unavailable, row.Availability);
        Assert.False(row.IsInstalled);
        Assert.Contains("runtime", row.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not downloaded", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnavailableModelOffersNoDownloadButtonToPressPointlessly()
    {
        // Pulling into a host that does not exist fails every time. Offering the button is how
        // somebody spends ten minutes clicking it.
        var provisioner = new FakeProvisioner
        {
            Host = ModelHostState.Missing("No runtime.", "Install the runtime first."),
        };

        var row = ModelRow.ForLlm(LlmModelCatalog.Cpu, provisioner, required: false);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(row.Download.CanExecute(null));
        Assert.Equal("Install the runtime first.", row.ErrorRemedy);
    }

    [Fact]
    public async Task AProbeThatThrowsIsUnavailableAndKeepsTheReason()
    {
        // Previously swallowed into "Not downloaded", which is how a connection refused on
        // 127.0.0.1:11435 came to be reported as a missing model.
        var provisioner = new FakeProvisioner
        {
            PresenceThrows = new HttpRequestException("Connection refused (127.0.0.1:11435)."),
        };

        var row = ModelRow.ForLlm(LlmModelCatalog.Gpu, provisioner, required: false);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.Unavailable, row.Availability);
        Assert.Contains("11435", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostIsARowOfItsOwnSoInstallingItIsOneClickWhereTheProblemIsShown()
    {
        var provisioner = new FakeProvisioner
        {
            Host = ModelHostState.Missing("Not found.", "Download it."),
        };

        var row = ModelRow.ForHost(provisioner);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.NotDownloaded, row.Availability);
        Assert.True(row.Download.CanExecute(null));

        await row.Download.ExecuteAsync(null);

        Assert.Contains("install-host", provisioner.Calls);
        Assert.True(row.IsInstalled);
    }

    [Fact]
    public async Task InstallingTheHostMakesTheLanguageModelRowsAskableAgain()
    {
        // The whole sequence, in the order a user does it: the rows are unavailable, the host is
        // installed, and a refresh turns them into ordinary downloadable rows.
        var provisioner = new FakeProvisioner
        {
            Host = ModelHostState.Missing("Not found.", "Download it."),
        };

        var model = ModelRow.ForLlm(LlmModelCatalog.Gpu, provisioner, required: false);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ModelAvailability.Unavailable, model.Availability);

        await ModelRow.ForHost(provisioner).Download.ExecuteAsync(null);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.NotDownloaded, model.Availability);
        Assert.True(model.Download.CanExecute(null));
    }

    [Fact]
    public async Task TheHostRowSaysWhatItIsAndWhatItCosts()
    {
        // 1.4 GB is a decision on a metered connection, and the licence is an obligation. Both
        // belong on the row, exactly as they do for the model weights.
        var row = ModelRow.ForHost(new FakeProvisioner());
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(row.SizeDescription));
        Assert.Equal("MIT", row.License);
        Assert.Contains("Ollama", row.Attribution, StringComparison.Ordinal);
        Assert.False(row.Required);
    }

    [Fact]
    public async Task AWeightsRowIsUnaffectedByTheHostBeingMissing()
    {
        // Parakeet and Silero are plain HTTPS downloads. A missing model runtime has nothing to do
        // with them, and dictation works with neither language model present.
        var provisioner = new FakeProvisioner
        {
            Host = ModelHostState.Missing("Not found.", "Download it."),
        };

        var row = ModelRow.ForAsset(Speech.ModelCatalog.ParakeetV2Int8, provisioner, required: true);
        await row.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelAvailability.NotDownloaded, row.Availability);
        Assert.True(row.Download.CanExecute(null));
    }
}
