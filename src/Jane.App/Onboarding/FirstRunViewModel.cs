using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Jane.App.Controls;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Settings;
using Jane.Core.Storage;
using Jane.Speech;

namespace Jane.App.Onboarding;

/// <summary>
/// First run, from "what is this" to a dictation that actually landed.
/// </summary>
/// <remarks>
/// <para>
/// The acceptance for this phase is: delete <c>%LOCALAPPDATA%\Jane</c>, launch, complete
/// onboarding <em>including the model pulls</em>, and dictate without touching documentation. So
/// the models step pulls the qwen3 tags as well as downloading the speech weights -- leaving a
/// manual <c>ollama pull</c> as homework would fail that acceptance even though everything on
/// screen said "done".
/// </para>
/// <para>
/// Nothing here is a modal dialog and nothing blocks. Every long operation reports progress, every
/// failure carries the remedy from its own typed exception, and a download that stopped half way
/// resumes on the next attempt -- including the next launch, because the partial file survives in
/// the model directory's staging folder.
/// </para>
/// <para>
/// Only two things are actually required to finish: the speech models, without which there is no
/// dictation at all, and nothing else. A microphone Jane cannot open, a language model that would
/// not pull, a test dictation somebody skipped -- all of those leave a usable Jane, and trapping
/// a first-time user on step two is worse than letting them finish and fix it in settings.
/// </para>
/// </remarks>
public sealed class FirstRunViewModel : ObservableObject, IDisposable
{
    /// <summary>Peak level that counts as "Jane heard you". Ordinary speech clears this easily.</summary>
    private const float HeardThreshold = 0.08f;

    private readonly SettingsRepository _settings;
    private readonly IModelProvisioner _provisioner;
    private readonly IMicrophoneCatalog _catalog;
    private readonly IMicrophoneCheck _microphoneCheck;
    private readonly Func<CancellationToken, Task<string?>> _testDictation;

    private JaneSettings _current;
    private OnboardingStep _step = OnboardingStep.Welcome;
    private MicrophoneChoice? _microphone;
    private IDisposable? _meter;
    private float _micLevel;
    private bool _heardYou;
    private string? _microphoneError;
    private HotkeyBinding _hotkey;
    private HotkeyValidation _hotkeyStatus;
    private string _scratchText = string.Empty;
    private bool _dictating;
    private string? _dictationError;
    private bool _busy;
    private bool _complete;
    private bool _disposed;

    /// <param name="testDictation">
    /// Runs one real dictation. Returns the text if the caller captured it, or null if the
    /// pipeline typed it straight into whatever had focus -- which, on this step, is the scratch
    /// box, and is the more honest test of the two.
    /// </param>
    public FirstRunViewModel(
        SettingsRepository settings,
        IModelProvisioner provisioner,
        IMicrophoneCatalog microphones,
        IMicrophoneCheck microphoneCheck,
        Func<CancellationToken, Task<string?>> testDictation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(microphones);
        ArgumentNullException.ThrowIfNull(microphoneCheck);
        ArgumentNullException.ThrowIfNull(testDictation);

        _settings = settings;
        _provisioner = provisioner;
        _catalog = microphones;
        _microphoneCheck = microphoneCheck;
        _testDictation = testDictation;

        _current = settings.Read();
        _hotkey = new HotkeyBinding(_current.Hotkey.VirtualKey, []);
        _hotkeyStatus = HotkeyValidator.Validate(_hotkey);

        Microphones = microphones.List();
        _microphone = Microphones.FirstOrDefault(m => m.Id == _current.MicrophoneDeviceId)
                      ?? Microphones.FirstOrDefault();

        foreach (var row in BuildRows())
        {
            Models.Add(row);
        }
    }

    /// <summary>Every step, with the words the window shows for it.</summary>
    public IReadOnlyList<OnboardingPage> Pages { get; } =
    [
        new(OnboardingStep.Welcome, "Welcome to Jane",
            "Jane types what you say into whatever you are already using. Every model runs on this machine, and nothing you dictate ever leaves it."),
        new(OnboardingStep.Microphone, "Can Jane hear you?",
            "Pick the microphone you want to dictate with and say something. Jane opens it in shared mode, only while you are actually dictating, so it never takes the device away from a call and never leaves a Bluetooth headset stuck in call mode."),
        new(OnboardingStep.Models, "Getting the models",
            "This is the only time Jane uses the network. Each file is pinned by address and checksum, and a download that stops picks up where it left off -- including after a restart."),
        new(OnboardingStep.Hotkey, "Choose your key",
            "Hold this key anywhere in Windows to dictate. Jane checks it against the combinations Windows will never deliver, and warns about the ones games tend to take."),
        new(OnboardingStep.TestDictation, "Try it",
            "Hold your key and say a sentence. It will appear in the box below, exactly as it would in any other application."),
        new(OnboardingStep.Done, "You are set up",
            "Jane lives in the tray from now on. Everything here can be changed later, and the history window shows every dictation with the application it went into."),
    ];

    public OnboardingStep Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                Raise(nameof(Page));
                Raise(nameof(StepNumber));
                Raise(nameof(StepIndicator));
                Raise(nameof(CanGoBack));
                Raise(nameof(CanGoNext));
                Raise(nameof(NextLabel));
                Raise(nameof(IsLastStep));
            }
        }
    }

    public OnboardingPage Page => Pages.First(p => p.Step == Step);

    /// <summary>1-based, for the "step 3 of 6" rail.</summary>
    public int StepNumber => (int)Step + 1;

    public int StepCount => Pages.Count;

    public string StepIndicator =>
        string.Create(CultureInfo.CurrentCulture, $"Step {StepNumber} of {StepCount}");

    public bool IsLastStep => Step == OnboardingStep.Done;

    public string NextLabel => Step switch
    {
        OnboardingStep.Welcome => "Get started",
        OnboardingStep.Done => "Finish",
        _ => "Continue",
    };

    /// <summary>True once onboarding has been recorded as complete in the database.</summary>
    public bool IsComplete
    {
        get => _complete;
        private set => Set(ref _complete, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(CanGoNext));
            }
        }
    }

    public bool CanGoBack => Step != OnboardingStep.Welcome && !IsBusy;

    /// <summary>
    /// Whether the current step is satisfied.
    /// </summary>
    /// <remarks>
    /// Only the models step can actually block, and only on the two files without which there is
    /// no dictation at all. Every other step is advisory: a microphone that will not open and a
    /// language model that will not pull both leave a Jane that works, and can both be fixed from
    /// settings afterwards.
    /// </remarks>
    public bool CanGoNext => !IsBusy && (Step != OnboardingStep.Models || RequiredModelsReady);

    // -------------------------------------------------------------- microphone

    public IReadOnlyList<MicrophoneChoice> Microphones { get; private set; }

    /// <summary>
    /// Re-enumerates the capture devices, keeping the chosen one if it is still there.
    /// </summary>
    /// <remarks>
    /// Somebody who reaches this step and realises their headset is unplugged should be able to
    /// plug it in and carry on, rather than restart onboarding. "Listen again" does this first.
    /// </remarks>
    public void RefreshMicrophones()
    {
        var chosen = _microphone?.Id;
        Microphones = _catalog.List();
        _microphone = Microphones.FirstOrDefault(m => m.Id == chosen) ?? Microphones.FirstOrDefault();

        Raise(nameof(Microphones));
        Raise(nameof(Microphone));
    }

    public MicrophoneChoice? Microphone
    {
        get => _microphone;
        set
        {
            if (!Set(ref _microphone, value))
            {
                return;
            }

            Persist(s => s with { MicrophoneDeviceId = value?.Id });

            if (Step == OnboardingStep.Microphone)
            {
                StartMicrophoneCheck();
            }
        }
    }

    /// <summary>Live peak level, 0 to 1. Drives the meter.</summary>
    public float MicrophoneLevel
    {
        get => _micLevel;
        private set => Set(ref _micLevel, value);
    }

    /// <summary>True once the level has passed the speaking threshold at least once.</summary>
    public bool HeardYou
    {
        get => _heardYou;
        private set
        {
            if (Set(ref _heardYou, value))
            {
                Raise(nameof(MicrophoneStatus));
                Raise(nameof(MicrophoneSeverity));
            }
        }
    }

    /// <summary>A sentence about why the device would not open, or null.</summary>
    public string? MicrophoneError
    {
        get => _microphoneError;
        private set
        {
            if (Set(ref _microphoneError, value))
            {
                Raise(nameof(MicrophoneStatus));
                Raise(nameof(MicrophoneSeverity));
            }
        }
    }

    public string MicrophoneStatus => this switch
    {
        { MicrophoneError: { Length: > 0 } error } => error,
        { HeardYou: true } => "Jane can hear you. That is all this step needed.",
        _ => "Say something. The bar moves when Jane hears you.",
    };

    public Severity MicrophoneSeverity => this switch
    {
        { MicrophoneError: not null } => Severity.Error,
        { HeardYou: true } => Severity.Success,
        _ => Severity.Info,
    };

    /// <summary>Opens the chosen device and starts metering. Safe to call repeatedly.</summary>
    public void StartMicrophoneCheck()
    {
        StopMicrophoneCheck();

        MicrophoneError = null;
        HeardYou = false;
        MicrophoneLevel = 0;

        if (Microphones.Count == 0)
        {
            MicrophoneError = "Windows reports no capture device at all. Plug a microphone in and reopen this step.";
            return;
        }

        _meter = _microphoneCheck.Start(
            Microphone?.Id,
            level =>
            {
                MicrophoneLevel = level;
                if (level >= HeardThreshold)
                {
                    HeardYou = true;
                }
            },
            failure => MicrophoneError = failure);
    }

    public void StopMicrophoneCheck()
    {
        _meter?.Dispose();
        _meter = null;
        MicrophoneLevel = 0;
    }

    // ------------------------------------------------------------------ models

    public ObservableCollection<ModelRow> Models { get; } = [];

    /// <summary>The two files without which there is no dictation at all.</summary>
    public IEnumerable<ModelRow> RequiredModels => Models.Where(m => m.Required);

    public bool RequiredModelsReady => RequiredModels.All(m => m.IsInstalled);

    /// <summary>The models step in one line, including whether anything failed.</summary>
    public string ModelSummary
    {
        get
        {
            var failed = Models.Count(m => m.Error is not null);
            if (failed > 0)
            {
                return failed == 1
                    ? "One download could not finish. The reason and what to do about it are on the row below."
                    : string.Create(CultureInfo.CurrentCulture,
                        $"{failed} downloads could not finish. The reason and what to do about each is on its row below.");
            }

            var ready = Models.Count(m => m.IsInstalled);
            if (ready == Models.Count)
            {
                return "Everything is on this machine. Jane will not use the network again.";
            }

            return RequiredModelsReady
                ? string.Create(CultureInfo.CurrentCulture,
                    $"{ready} of {Models.Count} ready. Dictation works now; the rest add transcript cleanup.")
                : string.Create(CultureInfo.CurrentCulture, $"{ready} of {Models.Count} ready.");
        }
    }

    /// <summary>
    /// Downloads and pulls everything onboarding needs, in dependency order.
    /// </summary>
    /// <remarks>
    /// One failure does not stop the rest. Somebody whose language-model pull failed should still
    /// end up with working dictation, and the row that failed keeps its own remedy and retry.
    /// </remarks>
    public async Task DownloadEverythingAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;

        try
        {
            foreach (var row in Models.Where(m => !m.IsInstalled))
            {
                await row.Download.ExecuteAsync(null);
                RaiseModelState();
            }
        }
        finally
        {
            IsBusy = false;
            RaiseModelState();
        }
    }

    /// <summary>Retries only the rows that failed, which is what the error state's button does.</summary>
    public async Task RetryFailedAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;

        try
        {
            foreach (var row in Models.Where(m => m.Error is not null).ToList())
            {
                await row.Download.ExecuteAsync(null);
                RaiseModelState();
            }
        }
        finally
        {
            IsBusy = false;
            RaiseModelState();
        }
    }

    /// <summary>Re-reads what is already present. Called on entering the step, and on relaunch.</summary>
    public async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        foreach (var row in Models)
        {
            await row.RefreshAsync(cancellationToken);
        }

        RaiseModelState();
    }

    // ------------------------------------------------------------------ hotkey

    public HotkeyBinding Hotkey
    {
        get => _hotkey;
        private set
        {
            if (Set(ref _hotkey, value))
            {
                Raise(nameof(HotkeyDescription));
            }
        }
    }

    public string HotkeyDescription => KeyNames.Describe(Hotkey);

    public HotkeyValidation HotkeyStatus
    {
        get => _hotkeyStatus;
        private set
        {
            if (Set(ref _hotkeyStatus, value))
            {
                Raise(nameof(HotkeySeverity));
            }
        }
    }

    public Severity HotkeySeverity => HotkeyStatus.Severity;

    /// <summary>Applies a proposed key, or refuses it and says why. Same rules as settings.</summary>
    public bool TryRebind(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var verdict = HotkeyValidator.Validate(binding);
        HotkeyStatus = verdict;

        if (!verdict.IsAcceptable)
        {
            return false;
        }

        Hotkey = binding;
        Persist(s => s with { Hotkey = s.Hotkey with { VirtualKey = binding.VirtualKey } });
        return true;
    }

    // --------------------------------------------------------- test dictation

    /// <summary>
    /// The scratch box's contents.
    /// </summary>
    /// <remarks>
    /// A real text box, focused, that the real injector types into -- not a transcript display.
    /// The point of this step is to prove the whole path including injection, and a label showing
    /// recognised text would prove only the half of it that was never in doubt.
    /// </remarks>
    public string ScratchText
    {
        get => _scratchText;
        set
        {
            if (Set(ref _scratchText, value))
            {
                Raise(nameof(HasDictated));
            }
        }
    }

    public bool HasDictated => !string.IsNullOrWhiteSpace(ScratchText);

    public bool IsDictating
    {
        get => _dictating;
        private set => Set(ref _dictating, value);
    }

    public string? DictationError
    {
        get => _dictationError;
        private set => Set(ref _dictationError, value);
    }

    /// <summary>Runs one real dictation into the scratch box.</summary>
    public async Task RunTestDictationAsync(CancellationToken cancellationToken)
    {
        IsDictating = true;
        DictationError = null;

        try
        {
            var text = await _testDictation(cancellationToken);

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Null means the pipeline typed straight into the focused box, which is the real
                // path; a returned string is the seam the tests and a headless run go through.
                ScratchText = string.IsNullOrEmpty(ScratchText) ? text : ScratchText + " " + text;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            DictationError = "That dictation did not complete: " + ex.Message;
        }
        finally
        {
            IsDictating = false;
        }
    }

    // ---------------------------------------------------------------- movement

    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        Enter((OnboardingStep)((int)Step - 1));
    }

    /// <summary>Moves to the next step, running whatever that step needs on entry.</summary>
    public async Task NextAsync(CancellationToken cancellationToken)
    {
        if (!CanGoNext)
        {
            return;
        }

        if (Step == OnboardingStep.Done)
        {
            await FinishAsync(cancellationToken);
            return;
        }

        await GoToAsync((OnboardingStep)((int)Step + 1), cancellationToken);
    }

    /// <summary>Jumps straight to a step, running its entry work.</summary>
    public async Task GoToAsync(OnboardingStep step, CancellationToken cancellationToken)
    {
        Enter(step);

        if (step == OnboardingStep.Models)
        {
            await RefreshModelsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Records that onboarding is done.
    /// </summary>
    /// <remarks>
    /// A flag in the database rather than a sentinel file, so deleting
    /// <c>%LOCALAPPDATA%\Jane</c> is the whole reset -- which is exactly what the acceptance step
    /// for this phase does.
    /// </remarks>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        StopMicrophoneCheck();

        Persist(s => s with { OnboardingComplete = true });
        await LastWrite;

        IsComplete = true;
    }

    /// <summary>The settings write in flight, or a completed task.</summary>
    public Task LastWrite { get; private set; } = Task.CompletedTask;

    private void Enter(OnboardingStep step)
    {
        if (step != OnboardingStep.Microphone)
        {
            // The device is released the moment the step is left. Holding a microphone open
            // behind a wizard page nobody is looking at is exactly the kind of thing that makes
            // a recording indicator sit there unexplained.
            StopMicrophoneCheck();
        }

        Step = step;

        if (step == OnboardingStep.Microphone)
        {
            StartMicrophoneCheck();
        }
    }

    private IEnumerable<ModelRow> BuildRows()
    {
        yield return ModelRow.ForAsset(ModelCatalog.ParakeetV2Int8, _provisioner, required: true);
        yield return ModelRow.ForAsset(ModelCatalog.SileroVad, _provisioner, required: true);

        foreach (var spec in LlmModelCatalog.Required)
        {
            yield return ModelRow.ForLlm(spec, _provisioner, required: false);
        }
    }

    private void RaiseModelState()
    {
        Raise(nameof(RequiredModelsReady));
        Raise(nameof(ModelSummary));
        Raise(nameof(CanGoNext));
    }

    private void Persist(Func<JaneSettings, JaneSettings> update)
    {
        _current = update(_current);
        LastWrite = _settings.WriteAsync(_current, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMicrophoneCheck();
    }
}
