using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Jane.App.Composition;
using Jane.App.Overlay;
using Jane.App.Tray;
using Jane.Core.Abstractions;

namespace Jane.App;

/// <summary>
/// Jane's process lifetime: a tray icon and a floating pill, and nothing else on screen.
/// </summary>
/// <remarks>
/// The dictation graph itself lives in <see cref="JaneHost"/>. This class owns only what is
/// genuinely about the application object: creating the two visuals, starting the host, and
/// shutting everything down in the right order.
/// <para>
/// Startup is deliberately not awaited on the UI thread. The recogniser's cold session-init
/// measured 1.4 s in Phase 1's bench, and blocking here would mean the tray icon does not appear
/// until it finishes -- so the icon appears immediately and the host reports readiness through
/// the tooltip. A hotkey pressed in that window waits for the engine rather than failing.
/// </para>
/// </remarks>
public partial class App : Application
{
    /// <summary>Set to "1" to start the shell without the dictation graph. Diagnostics only.</summary>
    public const string DisablePipelineVariable = "JANE_DISABLE_PIPELINE";

    /// <summary>Set to "1" to suppress the first-run window. Diagnostics and tests only.</summary>
    public const string SkipOnboardingVariable = "JANE_SKIP_ONBOARDING";

    private TrayIcon? _tray;
    private OverlayPresenter? _overlay;
    private JaneHost? _host;
    private WindowLauncher? _windows;

    /// <summary>The tray's command surface.</summary>
    public ITrayCommands? Tray => _tray;

    public IOverlayPresenter? Overlay => _overlay;

    public JaneHost? Host => _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The overlay's HWND is created here rather than on first dictation: building a window
        // costs a few milliseconds, and the hotkey's budget from key-down to armed is 50 ms.
        _overlay = new OverlayPresenter(Dispatcher);

        _tray = new TrayIcon();
        _tray.CommandInvoked += OnTrayCommand;
        _tray.Show();

        // A diagnostic seam, not a feature: JANE_DISABLE_PIPELINE=1 brings up the shell without
        // the dictation graph. It is what lets the idle-footprint test measure Jane's own cost
        // apart from the resident ASR model, which the plan accounts for separately.
        if (Environment.GetEnvironmentVariable(DisablePipelineVariable) != "1")
        {
            _ = StartHostAsync();
        }
    }

    private async Task StartHostAsync()
    {
        try
        {
            _host = JaneHost.Create(Dispatcher, _overlay!);
            _windows = new WindowLauncher(_host);

            await _host.StartAsync(CancellationToken.None);

            // First run: models are not downloaded and no hotkey has been chosen, so onboarding
            // runs before anything else. It is the only window Jane ever opens by itself.
            // A test-launched Jane must never open onboarding: it would steal the foreground and
            // stop being idle, which is the one thing the idle-footprint test is measuring.
            if (!_host.Settings.OnboardingComplete &&
                Environment.GetEnvironmentVariable(SkipOnboardingVariable) != "1")
            {
                await _windows.RunOnboardingAsync();
            }
        }
        catch (Exception ex)
        {
            // Startup failing must not take the process down: the tray icon stays, so the user can
            // reach settings, see the message and download whatever is missing.
            _overlay?.Show(new OverlayStatus(OverlayState.Error, DescribeStartupFailure(ex)));
        }
    }

    private static string DescribeStartupFailure(Exception ex) => ex switch
    {
        Speech.VadModelMissingException => "Voice detection model missing. Open Jane's settings to download it.",
        Speech.SpeechModelMissingException => "Speech model missing. Open Jane's settings to download it.",
        _ => "Jane could not start its dictation pipeline. See the log for details.",
    };

    protected override void OnExit(ExitEventArgs e)
    {
        // Host first: it owns the keyboard hook and the supervised child processes, and both
        // must be torn down before the visuals they report into disappear.
        if (_host is not null)
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _tray?.Dispose();
        _overlay?.Dispose();

        base.OnExit(e);
    }

    private void OnTrayCommand(object? sender, TrayCommandEventArgs e)
    {
        switch (e.Command)
        {
            case TrayCommand.Quit:
                Shutdown();
                break;

            case TrayCommand.Pause:
                _host?.SetPaused(e.IsChecked);
                break;

            case TrayCommand.ModeHoldToTalk:
                _host?.SetMode(HotkeyMode.Hold);
                break;

            case TrayCommand.ModeToggle:
                _host?.SetMode(HotkeyMode.Toggle);
                break;

            case TrayCommand.Settings:
                _windows?.ShowSettings();
                break;

            case TrayCommand.History:
                _windows?.ShowHistory();
                break;

            default:
                // Dictate and Autostart need nothing here: the tray settles autostart itself, and
                // "dictate" is the hotkey, which is always listening.
                break;
        }
    }
}
