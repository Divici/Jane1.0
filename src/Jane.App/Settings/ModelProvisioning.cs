using Jane.Core.Models;
using Jane.Speech;

namespace Jane.App.Settings;

/// <param name="Completed">Bytes written so far, as Ollama's <c>/api/pull</c> reports them.</param>
/// <param name="Status">Ollama's own status line -- "pulling manifest", "verifying sha256digest".</param>
public sealed record LlmPullProgress(string Model, long Completed, long Total, string Status)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Completed / (double)Total, 0, 1);
}

/// <param name="Tag">The upstream Ollama tag, exactly as <c>ollama pull</c> takes it.</param>
/// <param name="Role">One line saying what Jane uses it for.</param>
public sealed record LlmModelSpec(string Tag, string Role, string SizeDescription, string License, string Attribution);

/// <summary>
/// The language models onboarding pulls, mirroring <c>NOTICE.md</c>.
/// </summary>
/// <remarks>
/// The GPU tag is <c>qwen3:4b-instruct</c> rather than <c>qwen3:4b</c>, and that is not a typo.
/// P0-2 in the implementation log measured <c>qwen3:4b</c> spending 131-203 tokens on reasoning
/// for a two-token answer because its template opens a <c>&lt;think&gt;</c> block
/// unconditionally, which <c>think: false</c> does not close. The dedicated non-thinking
/// checkpoint answers the same prompt in two tokens.
/// </remarks>
public static class LlmModelCatalog
{
    public static LlmModelSpec Gpu { get; } = new(
        "qwen3:4b-instruct",
        "Cleans and formats the transcript on the GPU.",
        "2.5 GB",
        "Apache-2.0",
        "Qwen3-4B-Instruct-2507 by the Qwen Team, Alibaba Cloud. Licensed Apache-2.0.");

    public static LlmModelSpec Cpu { get; } = new(
        "qwen3:1.7b",
        "The CPU route, for when the GPU is busy and you have opted into formatting anyway.",
        "1.4 GB",
        "Apache-2.0",
        "Qwen3-1.7B by the Qwen Team, Alibaba Cloud. Licensed Apache-2.0.");

    public static IReadOnlyList<LlmModelSpec> Required { get; } = [Gpu, Cpu];
}

/// <summary>Pulls one model through the supervised Ollama instance's <c>/api/pull</c>.</summary>
public delegate Task LlmModelPull(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken);

/// <summary>Asks the supervised Ollama instance whether a tag is already local.</summary>
public delegate Task<bool> LlmModelPresent(string model, CancellationToken cancellationToken);

/// <summary>
/// Everything the settings and onboarding windows need in order to put a model on this machine.
/// </summary>
/// <remarks>
/// <para>
/// One interface over two very different mechanisms -- a pinned HTTPS download that Jane verifies
/// and unpacks itself, and an <c>ollama pull</c> that Jane only supervises -- because the windows
/// treat them identically: a row with a size, a licence, a progress bar and a retry.
/// </para>
/// <para>
/// It exists so the tests can stub both. The alternative is an onboarding test that downloads
/// 482 MB, which is not a test anybody runs.
/// </para>
/// </remarks>
/// <summary>
/// Whether the thing that runs the language models exists on this machine at all.
/// </summary>
/// <remarks>
/// A separate question from whether a model is downloaded, and the reason the two were confused
/// for months: with no runtime, "is qwen3:4b-instruct present?" is not false, it is unanswerable.
/// The published build shipped without one and every row read "Not downloaded" as a result.
/// </remarks>
/// <param name="Detail">What is true right now, in a sentence. Shown on the runtime's own row.</param>
/// <param name="Remedy">What the user can do about it. Null when there is nothing to do.</param>
public sealed record ModelHostState(bool Available, string Detail, string? Remedy = null)
{
    public static ModelHostState Ready(string detail) => new(true, detail);

    public static ModelHostState Missing(string detail, string remedy) => new(false, detail, remedy);
}

public interface IModelProvisioner
{
    bool IsInstalled(ModelAsset asset);

    /// <exception cref="ModelDownloadException">
    /// Offline, HTTP error, checksum mismatch, disk full or extraction failure -- each carrying
    /// its own <c>Remedy</c>, which is what the error state shows.
    /// </exception>
    Task EnsureAsync(ModelAsset asset, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);

    Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken);

    Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Whether the model runtime is present, re-read on each call.</summary>
    ModelHostState Host { get; }

    /// <summary>
    /// Downloads and unpacks the model runtime.
    /// </summary>
    /// <remarks>
    /// Here rather than behind a documentation link because the user who needs it is the one who
    /// installed Jane from the published artifact, and has no repository checkout to run
    /// <c>build/get-ollama.ps1</c> from.
    /// </remarks>
    Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The real provisioner: <see cref="ModelDownloader"/> for weights, injected delegates for Ollama.
/// </summary>
/// <remarks>
/// The Ollama half arrives as delegates rather than as a typed client because the supervisor
/// lives in <c>Jane.Llm</c> and owns the child process's lifetime. Handing the window a
/// supervisor would let a settings screen restart a server; handing it two functions cannot.
/// </remarks>
public sealed class ModelProvisioner(
    ModelDownloader downloader,
    LlmModelPull pull,
    LlmModelPresent present,
    ModelHostProvisioning host)
    : IModelProvisioner
{
    public bool IsInstalled(ModelAsset asset) => downloader.IsInstalled(asset);

    public Task EnsureAsync(
        ModelAsset asset,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => downloader.EnsureAsync(asset, progress, cancellationToken);

    public Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken) =>
        present(model, cancellationToken);

    public Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken) =>
        pull(model, progress, cancellationToken);

    public ModelHostState Host => host.State();

    public Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken) =>
        host.InstallAsync(progress, cancellationToken);
}

/// <summary>
/// The model runtime's half of provisioning: is it here, and how do we get it.
/// </summary>
/// <remarks>
/// Two delegates rather than a typed installer, for the same reason the Ollama pull is a delegate:
/// the composition root owns the runtime's location and the supervised process's lifetime, and a
/// settings window holding either could restart a server.
/// </remarks>
/// <param name="State">Re-evaluated on every read, because installing the runtime changes it.</param>
public sealed record ModelHostProvisioning(
    Func<ModelHostState> State,
    Func<IProgress<ModelDownloadProgress>?, CancellationToken, Task> InstallAsync)
{
    /// <summary>For a Jane built with no LLM at all: nothing to report, nothing to install.</summary>
    public static ModelHostProvisioning Disabled { get; } = new(
        () => ModelHostState.Ready("Transcript cleanup is switched off in settings."),
        (_, _) => Task.CompletedTask);
}

/// <summary>
/// A provisioner for a machine with no Ollama.
/// </summary>
/// <remarks>
/// Skipping the LLM entirely is a supported outcome -- it is what the in-game route does every
/// time, and Parakeet already emits punctuation and casing. So "no Ollama" reports the models as
/// absent and refuses the pull with a sentence saying that, rather than throwing something the
/// window would have to translate.
/// </remarks>
public sealed class WeightsOnlyProvisioner(ModelDownloader downloader, ModelHostProvisioning host)
    : IModelProvisioner
{
    public bool IsInstalled(ModelAsset asset) => downloader.IsInstalled(asset);

    public Task EnsureAsync(
        ModelAsset asset,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => downloader.EnsureAsync(asset, progress, cancellationToken);

    public Task<bool> IsPulledAsync(string model, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task PullAsync(string model, IProgress<LlmPullProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromException(new ModelDownloadException(
            ModelDownloadFailure.ServiceUnreachable,
            $"There is no model runtime on this machine, so {model} cannot be pulled. Jane still dictates without it: the raw transcript already carries punctuation and casing."));

    public ModelHostState Host => host.State();

    public Task InstallHostAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken) =>
        host.InstallAsync(progress, cancellationToken);
}

/// <summary>
/// An <see cref="IProgress{T}"/> that calls back on the reporting thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to the captured synchronisation context, which on the UI
/// thread means a report raised just before an await is delivered just after it. That makes
/// "did the bar move" a race in the tests and, worse, leaves the last stage of a finished
/// download arriving after the row has already been marked installed. Every consumer here only
/// raises property changes, which WPF marshals to the dispatcher on its own, so calling straight
/// through is both simpler and more correct.
/// </remarks>
public sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
