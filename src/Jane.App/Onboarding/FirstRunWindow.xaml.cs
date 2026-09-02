using System.Windows;
using System.Windows.Controls;
using Jane.App.Controls;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.Storage;

namespace Jane.App.Onboarding;

/// <summary>
/// First run: a six-step wizard that ends with a dictation that actually landed.
/// </summary>
/// <remarks>
/// <para>
/// The window is a shell around <see cref="FirstRunViewModel"/> and owns no state of its own. What
/// it does own is the one thing a view model cannot: focus. The test-dictation step puts the caret
/// in the scratch box before running the pipeline, because the whole point of that step is that
/// Jane's real Win32 injection path types into whatever holds the caret, and a step that typed
/// into a label would be testing nothing.
/// </para>
/// <para>
/// The model provisioner and the test dictation both arrive as injected seams. Onboarding
/// downloads 482 MB of speech weights and pulls two language models through Ollama, and a test
/// that did any of that for real is a test nobody would run.
/// </para>
/// </remarks>
public partial class FirstRunWindow : Window
{
    /// <param name="testDictation">
    /// Runs one real dictation. Returns the recognised text, or null when the pipeline typed it
    /// straight into the focused scratch box -- which is what the composition root wires.
    /// </param>
    public FirstRunWindow(
        SettingsRepository settings,
        IModelProvisioner provisioner,
        IMicrophoneCatalog microphones,
        IMicrophoneCheck microphoneCheck,
        Func<CancellationToken, Task<string?>> testDictation)
        : this(new FirstRunViewModel(settings, provisioner, microphones, microphoneCheck, testDictation))
    {
    }

    public FirstRunWindow(FirstRunViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = model;
        InitializeComponent();

        DataContext = model;
        JaneChrome.Apply(this);

        Model.PropertyChanged += OnModelChanged;
        Steps.SelectedItem = ItemFor(Model.Step);
    }

    public FirstRunViewModel Model { get; }

    /// <summary>The footer, which holds Back and Continue. Walked separately by the a11y test.</summary>
    public FrameworkElement Chrome => Footer;

    public FrameworkElement Page => Root;

    public ToastHost Toasts => ToastLayer;

    /// <summary>One step's content, whether or not it has ever been on screen.</summary>
    public FrameworkElement StepView(OnboardingStep step) => (FrameworkElement)ItemFor(step).Content;

    private TabItem ItemFor(OnboardingStep step) =>
        Steps.Items.OfType<TabItem>().First(item => (OnboardingStep?)item.Tag == step);

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FirstRunViewModel.Step))
        {
            return;
        }

        Steps.SelectedItem = ItemFor(Model.Step);

        // The scratch box has to hold the caret before a test dictation runs, because Jane injects
        // into whatever does. Moving focus here rather than in the view model keeps the model free
        // of the visual tree.
        if (Model.Step == OnboardingStep.TestDictation)
        {
            _ = Dispatcher.BeginInvoke(() => Scratch.Focus());
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        await Model.NextAsync(CancellationToken.None);

        if (Model.IsComplete)
        {
            Close();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => Model.Back();

    private void OnRestartMicrophone(object sender, RoutedEventArgs e)
    {
        // Re-enumerate first: the most likely reason somebody presses this is that they have
        // just plugged something in.
        Model.RefreshMicrophones();
        Model.StartMicrophoneCheck();
    }

    private async void OnDownloadEverything(object sender, RoutedEventArgs e)
    {
        await Model.DownloadEverythingAsync(CancellationToken.None);

        if (Model.RequiredModelsReady)
        {
            Toasts.Show("Speech models are ready", Severity.Success);
        }
    }

    private async void OnRetryFailed(object sender, RoutedEventArgs e) =>
        await Model.RetryFailedAsync(CancellationToken.None);

    private void OnHotkeyCaptured(object? sender, HotkeyBinding binding)
    {
        if (!Model.TryRebind(binding))
        {
            Toasts.Show("That key cannot be used", Severity.Error, Model.HotkeyStatus.Message);
        }
    }

    private void OnUseDefaultHotkey(object sender, RoutedEventArgs e) => Model.TryRebind(HotkeyBinding.Default);

    private async void OnRunTestDictation(object sender, RoutedEventArgs e)
    {
        Scratch.Focus();
        Scratch.CaretIndex = Scratch.Text.Length;

        await Model.RunTestDictationAsync(CancellationToken.None);
    }

    private void OnClearScratch(object sender, RoutedEventArgs e)
    {
        Model.ScratchText = string.Empty;
        Scratch.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        Model.PropertyChanged -= OnModelChanged;
        Model.Dispose();
        base.OnClosed(e);
    }
}
