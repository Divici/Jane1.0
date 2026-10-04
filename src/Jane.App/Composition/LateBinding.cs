using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Models;
using Jane.Core.Pipeline;
using Jane.Speech;

namespace Jane.App.Composition;

/// <summary>
/// A formatter whose behaviour can be supplied after the pipeline that uses it has been built.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator takes its formatter once, at construction, and keeps it. That was fine while
/// "is there a language model" was a fact about the machine at launch. On a machine Jane has just
/// been installed on it is not: the model runtime arrives part-way through first run, and before
/// this existed the only way to use it was to quit Jane and start it again -- which nothing on
/// screen suggested.
/// </para>
/// <para>
/// Unbound, it passes text through untouched, which is the same thing a Jane with transcript
/// cleanup switched off does. The raw transcript already carries punctuation and casing.
/// </para>
/// </remarks>
public sealed class SwitchableFormatter : ITranscriptFormatter
{
    private ITranscriptFormatter? _inner;

    public bool IsBound => Volatile.Read(ref _inner) is not null;

    public void Bind(ITranscriptFormatter inner) => Volatile.Write(ref _inner, inner);

    public Task<string> FormatAsync(string transcript, FormattingContext context, CancellationToken cancellationToken) =>
        Volatile.Read(ref _inner) is { } inner
            ? inner.FormatAsync(transcript, context, cancellationToken)
            : Task.FromResult(transcript);
}

/// <summary>The Edit Mode counterpart of <see cref="SwitchableFormatter"/>.</summary>
/// <remarks>
/// Unbound, it behaves as <see cref="UnavailableRewriter"/>: a rewrite is refused rather than
/// guessed at, and Edit Mode's local operations still work.
/// </remarks>
public sealed class SwitchableRewriter : ISelectionRewriter
{
    private ISelectionRewriter _inner = UnavailableRewriter.Instance;

    public void Bind(ISelectionRewriter inner) => Volatile.Write(ref _inner, inner);

    public Task<string> RewriteAsync(
        string selection, string instruction, FormattingContext context, CancellationToken cancellationToken) =>
        Volatile.Read(ref _inner).RewriteAsync(selection, instruction, context, cancellationToken);
}

/// <summary>The two things a running language-model server can be asked.</summary>
public sealed record LanguageModelAccess(LlmModelPull Pull, LlmModelPresent Present);

/// <summary>
/// Model provisioning whose language-model half is found when it is asked for, not when Jane
/// starts.
/// </summary>
/// <remarks>
/// <para>
/// There used to be two provisioners, chosen once at launch: one for a machine with a model
/// runtime, one for a machine without. The settings window and the wizard were handed whichever
/// it was and kept it. So installing the runtime from the wizard changed nothing the wizard could
/// see -- the row below it, the model itself, still belonged to the "no runtime" provisioner and
/// refused.
/// </para>
/// <para>
/// This asks each time. <paramref name="languageModels"/> brings the server up if it can and
/// returns null if it cannot, and installing the runtime calls it straight away so the next row
/// works.
/// </para>
/// </remarks>
/// <param name="weights">
/// Downloads speech models and installs the runtime. Its own answers about language models are
/// what is said when there is no server: "not pulled", and a refusal that explains itself.
/// </param>
/// <param name="languageModels">Brings the language-model server up if the machine has one.</param>
/// <param name="weightsInstalled">
/// Called after a model finishes downloading, so the pipeline can start loading it.
/// </param>
public sealed class LateBoundProvisioner(
    IModelProvisioner weights,
    Func<CancellationToken, Task<LanguageModelAccess?>> languageModels,
    Action weightsInstalled) : IModelProvisioner
{
    public bool IsInstalled(ModelAsset asset) => weights.IsInstalled(asset);

    public async Task EnsureAsync(
        ModelAsset asset, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await weights.EnsureAsync(asset, progress, cancellationToken);
        weightsInstalled();
    }

    public async Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken)
    {
        var access = await languageModels(cancellationToken);
        return access is not null && await access.Present(model, cancellationToken);
    }

    public async Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken)
    {
        var access = await languageModels(cancellationToken);
        if (access is null)
        {
            await weights.PullAsync(model, progress, cancellationToken);
            return;
        }

        await access.Pull(model, progress, cancellationToken);
    }

    public ModelHostState Host => weights.Host;

    public async Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await weights.InstallHostAsync(progress, cancellationToken);

        // Now, rather than whenever somebody next asks. The user is looking at a list whose next
        // row needs this server, and the first thing it does is start a process.
        await languageModels(cancellationToken);
    }
}

/// <summary>What Jane says when it could not start, or has not finished starting.</summary>
/// <remarks>
/// In one place so the overlay, the tray and the tests agree, and so no exception's type name or
/// message ever reaches the screen: those go to the log.
/// </remarks>
public static class StartupFailure
{
    public static string Describe(Exception failure) => failure switch
    {
        VadModelMissingException => "Voice detection model missing. Open Jane's settings to download it.",
        SpeechModelMissingException => "Speech model missing. Open Jane's settings to download it.",
        _ => "Jane could not start its dictation pipeline. See the log for details.",
    };

    /// <summary>
    /// What a menu item says when the window it opens is not available.
    /// </summary>
    /// <param name="startupFailure">The message already shown for a failed start, if there was one.</param>
    public static string WindowUnavailable(string? startupFailure) =>
        startupFailure is null
            ? "Jane is still starting. Try again in a moment."
            : $"{startupFailure.TrimEnd('.')}. Use \"Open log folder\" in this menu to see why.";
}
