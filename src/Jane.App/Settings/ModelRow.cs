using System.Globalization;
using System.IO;
using System.Net.Http;
using Jane.App.Controls;
using Jane.Core.Models;
using Jane.Speech;

namespace Jane.App.Settings;

/// <param name="Stage">Ollama's or the downloader's own word for what it is doing right now.</param>
public sealed record ModelStep(double Fraction, string Stage);

/// <summary>
/// The three things that can be true of a model, where there used to be two.
/// </summary>
/// <remarks>
/// A boolean forced "I could not find out" to be reported as "it is not there", which is how a
/// refused connection to Jane's own model runtime came to be shown as two undownloaded models next
/// to a button that could never succeed.
/// </remarks>
public enum ModelAvailability
{
    /// <summary>Asked, and it is here.</summary>
    Installed,

    /// <summary>Asked, and it is not here. Downloading it is the remedy.</summary>
    NotDownloaded,

    /// <summary>Could not ask. Downloading is not the remedy, and is not offered.</summary>
    Unavailable,
}

/// <param name="Reason">Why it could not be asked. Only set for <see cref="ModelAvailability.Unavailable"/>.</param>
/// <param name="Remedy">What to do about it, when there is something.</param>
public sealed record ModelPresence(ModelAvailability Availability, string? Reason = null, string? Remedy = null)
{
    public static ModelPresence Installed { get; } = new(ModelAvailability.Installed);

    public static ModelPresence Missing { get; } = new(ModelAvailability.NotDownloaded);

    public static ModelPresence Unavailable(string reason, string? remedy = null) =>
        new(ModelAvailability.Unavailable, reason, remedy);

    public static ModelPresence For(bool installed) => installed ? Installed : Missing;
}

/// <summary>
/// One model in the management list: what it is, what it costs, what it is licensed under, and a
/// button that puts it on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The same row type serves a pinned HTTPS download that Jane verifies and unpacks itself and an
/// <c>ollama pull</c> Jane only supervises, because to the person looking at the list they are
/// the same thing. What differs -- and what the row therefore takes as delegates -- is how you
/// ask whether it is there and how you make it be there.
/// </para>
/// <para>
/// The size and the licence are on the row rather than in a footnote. A 482 MB download over a
/// metered connection is a decision, and CC-BY-4.0 is an obligation; both belong at the moment of
/// the click.
/// </para>
/// </remarks>
public sealed class ModelRow : ObservableObject
{
    private readonly Func<CancellationToken, Task<ModelPresence>> _isPresent;
    private readonly Func<IProgress<ModelStep>, CancellationToken, Task> _install;

    private ModelPresence _presence = ModelPresence.Missing;
    private double _progress;
    private string _stage = string.Empty;
    private ModelDownloadException? _error;
    private bool _busy;

    public ModelRow(
        string id,
        string title,
        string role,
        string sizeDescription,
        string license,
        string attribution,
        bool required,
        Func<CancellationToken, Task<ModelPresence>> isPresent,
        Func<IProgress<ModelStep>, CancellationToken, Task> install)
    {
        Id = id;
        Title = title;
        Role = role;
        SizeDescription = sizeDescription;
        License = license;
        Attribution = attribution;
        Required = required;
        _isPresent = isPresent;
        _install = install;

        // Unavailable is deliberately not downloadable. Pulling into a runtime that does not exist
        // fails identically every time, and offering the button is how somebody spends ten minutes
        // clicking it.
        Download = new AsyncRelayCommand(
            (_, token) => RunAsync(token),
            _ => Availability != ModelAvailability.Unavailable && (!IsInstalled || Error is not null));
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>One line saying what Jane uses it for.</summary>
    public string Role { get; }

    public string SizeDescription { get; }

    public string License { get; }

    /// <summary>The attribution line, shown on the row and mirrored in About and NOTICE.md.</summary>
    public string Attribution { get; }

    /// <summary>Whether dictation works at all without it. Drives the "Required" badge.</summary>
    public bool Required { get; }

    public AsyncRelayCommand Download { get; }

    /// <summary>Here, absent, or unaskable. The third is why this is not a boolean.</summary>
    public ModelAvailability Availability => _presence.Availability;

    /// <summary>Why the model could not be asked about. Null unless <see cref="Availability"/> says so.</summary>
    public string? UnavailableReason => _presence.Reason;

    public bool IsInstalled
    {
        get => _presence.Availability == ModelAvailability.Installed;
        private set => Presence = value ? ModelPresence.Installed : ModelPresence.Missing;
    }

    private ModelPresence Presence
    {
        get => _presence;
        set
        {
            if (Set(ref _presence, value))
            {
                Raise(nameof(IsInstalled));
                Raise(nameof(Availability));
                Raise(nameof(UnavailableReason));
                Raise(nameof(ErrorRemedy));
                Raise(nameof(StatusText));
                Raise(nameof(StatusSeverity));
                Download.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>0 to 1. Meaningful only while <see cref="IsBusy"/>, and 1 once installed.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                Raise(nameof(ProgressPercent));
            }
        }
    }

    public int ProgressPercent => (int)Math.Round(Progress * 100);

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(StatusSeverity));
            }
        }
    }

    /// <summary>The downloader's own stage word: "downloading", "verifying", "installing".</summary>
    public string Stage
    {
        get => _stage;
        private set => Set(ref _stage, value);
    }

    /// <summary>The last failure, kept so its <c>Remedy</c> can be shown. Null when all is well.</summary>
    public ModelDownloadException? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(ErrorRemedy));
                Raise(nameof(CanRetry));
                Raise(nameof(StatusText));
                Raise(nameof(StatusSeverity));
                Download.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What the user can actually do about it, whether it failed or was never askable.</summary>
    public string? ErrorRemedy => Error?.Remedy ?? _presence.Remedy;

    /// <summary>Every download failure is retryable, and a partial download resumes where it stopped.</summary>
    public bool CanRetry => Error is not null;

    /// <summary>
    /// Whether the last attempt picked up a partial download rather than starting over.
    /// </summary>
    /// <remarks>
    /// Read from the first progress report of the run: a download that begins part-way through
    /// is one that found bytes already on disk. Worth surfacing, because "downloading 482 MB
    /// again" and "finishing the last 200 MB" are very different propositions to somebody whose
    /// connection just dropped.
    /// </remarks>
    public bool Resumed
    {
        get => _resumed;
        private set => Set(ref _resumed, value);
    }

    private bool _resumed;
    private bool _awaitingFirstReport;

    public string StatusText
    {
        get
        {
            if (IsBusy)
            {
                return string.IsNullOrEmpty(Stage)
                    ? "Working..."
                    : string.Create(CultureInfo.CurrentCulture, $"{Stage}  {ProgressPercent}%");
            }

            if (Error is not null)
            {
                return Error.Message;
            }

            return Availability switch
            {
                ModelAvailability.Installed => "Installed",
                ModelAvailability.Unavailable => UnavailableReason ?? "Cannot be checked right now",
                _ => "Not downloaded",
            };
        }
    }

    public Severity StatusSeverity => this switch
    {
        { Error: not null } => Severity.Error,
        { IsBusy: true } => Severity.Info,
        { IsInstalled: true } => Severity.Success,
        _ => Severity.Warning,
    };

    /// <summary>Re-reads whether the model is present. Cheap; called when the section opens.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            Presence = await _isPresent(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // Not being able to ask is not the same as the model being absent. Reporting it as
            // absent is what turned "nothing is listening on 127.0.0.1:11435" into two rows
            // reading "Not downloaded" with a button that could not have worked.
            Presence = ModelPresence.Unavailable(ex.Message);
        }

        if (IsInstalled)
        {
            Progress = 1;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Error = null;
        IsBusy = true;
        Stage = "starting";
        Progress = 0;
        Resumed = false;
        _awaitingFirstReport = true;

        var progress = new ImmediateProgress<ModelStep>(step =>
        {
            if (_awaitingFirstReport)
            {
                _awaitingFirstReport = false;
                Resumed = step.Fraction is > 0 and < 1;
            }

            Progress = step.Fraction;
            Stage = step.Stage;
        });

        try
        {
            await _install(progress, cancellationToken);
            IsInstalled = true;
            Progress = 1;
            Stage = string.Empty;
        }
        catch (ModelDownloadException ex)
        {
            Error = ex;
            IsInstalled = false;
        }
        catch (OperationCanceledException)
        {
            Stage = "cancelled";
        }
        catch (Exception ex)
        {
            // Anything the provisioner did not classify still has to reach the user as a remedy
            // rather than as a stack trace, so it is wrapped rather than rethrown. Not as an
            // HttpError outright: a refused loopback connection is Jane's own language-model
            // service being absent, and telling somebody the release asset may have moved sends
            // them to retry a download that cannot ever succeed.
            Error = ModelDownloadException.FromUnclassified(ex);
            IsInstalled = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A row for one pinned weight file, downloaded and verified by Jane itself.</summary>
    public static ModelRow ForAsset(ModelAsset asset, IModelProvisioner provisioner, bool required)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(provisioner);

        return new ModelRow(
            asset.Id,
            Describe(asset),
            RoleOf(asset),
            asset.SizeDescription,
            asset.License,
            asset.Attribution,
            required,
            _ => Task.FromResult(ModelPresence.For(provisioner.IsInstalled(asset))),
            (progress, token) => provisioner.EnsureAsync(
                asset,
                new ImmediateProgress<ModelDownloadProgress>(p => progress.Report(new ModelStep(p.Fraction, p.Stage))),
                token));
    }

    /// <summary>A row for one Ollama tag, pulled through the supervised instance.</summary>
    public static ModelRow ForLlm(LlmModelSpec spec, IModelProvisioner provisioner, bool required)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(provisioner);

        return new ModelRow(
            spec.Tag,
            spec.Tag,
            spec.Role,
            spec.SizeDescription,
            spec.License,
            spec.Attribution,
            required,
            async token =>
            {
                // Asked first, because with no runtime the pull probe is a connection to a port
                // nobody is listening on -- which answers "no" and means "cannot say".
                var host = provisioner.Host;
                return host.Available
                    ? ModelPresence.For(await provisioner.IsPulledAsync(spec.Tag, token))
                    : ModelPresence.Unavailable(host.Detail, host.Remedy);
            },
            (progress, token) => provisioner.PullAsync(
                spec.Tag,
                new ImmediateProgress<LlmPullProgress>(p => progress.Report(new ModelStep(p.Fraction, p.Status))),
                token));
    }

    /// <summary>
    /// A row for the model runtime itself.
    /// </summary>
    /// <remarks>
    /// The runtime is listed alongside the models rather than hidden behind a settings link,
    /// because "the thing that runs the models is missing" and "a model is missing" are the same
    /// problem to the person looking at the list, and the fix has to be in the place the problem
    /// is shown. It is not <see cref="Required"/>: dictation works without any of it.
    /// </remarks>
    public static ModelRow ForHost(IModelProvisioner provisioner)
    {
        ArgumentNullException.ThrowIfNull(provisioner);

        return new ModelRow(
            "ollama-runtime",
            "Ollama runtime",
            "Runs the language models that clean up transcripts. Jane supervises its own copy on port 11435 and never touches an Ollama desktop app on 11434.",
            "1.4 GB",
            "MIT",
            "Ollama by Ollama Inc. Licensed MIT. Jane bundles the standalone release archive, not the desktop package.",
            required: false,
            _ => Task.FromResult(ModelPresence.For(provisioner.Host.Available)),
            (progress, token) => provisioner.InstallHostAsync(
                new ImmediateProgress<ModelDownloadProgress>(p => progress.Report(new ModelStep(p.Fraction, p.Stage))),
                token));
    }

    private static string Describe(ModelAsset asset) => asset.Id switch
    {
        "parakeet-tdt-0.6b-v2-int8" => "Parakeet TDT 0.6B v2 (int8)",
        "silero-vad" => "Silero VAD",
        "whisper-large-v3-turbo-q5_0" => "Whisper large-v3-turbo (q5_0)",
        "whisper-large-v3-turbo-q8_0" => "Whisper large-v3-turbo (q8_0)",
        _ => asset.Id,
    };

    private static string RoleOf(ModelAsset asset) => asset.Id switch
    {
        "parakeet-tdt-0.6b-v2-int8" =>
            "The speech engine. English only, runs on the CPU, holds no VRAM, and emits punctuation and casing itself.",
        "silero-vad" =>
            "Detects whether you actually spoke and trims the silence either side.",
        _ =>
            "The second speech engine, kept so the benchmark has something to compare against.",
    };
}
