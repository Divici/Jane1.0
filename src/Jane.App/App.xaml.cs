using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Jane.App.Composition;
using Jane.App.Overlay;
using Jane.App.Tray;
using Jane.Core.Abstractions;
using Jane.Core.Diagnostics;
using Jane.Core.Platform;

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
    private SingleInstance? _instance;

    /// <summary>The tray's command surface.</summary>
    public ITrayCommands? Tray => _tray;

    public IOverlayPresenter? Overlay => _overlay;

    public JaneHost? Host => _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything that touches the keyboard, the microphone or the screen. A second Jane
        // installs a second hook on the same key, opens the same capture device against the first,
        // and draws a second pill over the first one -- which is how this was found, as a resting
        // pill with two different strings rendered on top of each other.
        _instance = SingleInstance.Acquire();
        if (!_instance.IsOwner)
        {
            // Tell the copy that is already running, so it can surface itself. Exiting in silence
            // is indistinguishable from the launch having done nothing, which is what makes
            // somebody click the icon another four times.
            _instance.NotifyOwner();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        _instance.SecondInstanceAttempted += OnSecondInstanceAttempted;

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
            var decision = StartupPolicy.Decide(
                _host.Settings,
                StartupPolicy.SkipRequested(Environment.GetEnvironmentVariable(SkipOnboardingVariable)));

            _host.Log.Write(LogLevel.Info, "startup", "Onboarding decision.", LogFields.New()
                .Add("run", decision.ShouldRunOnboarding)
                .Add("why", decision.Reason));

            if (decision.ShouldRunOnboarding)
            {
                await _windows.RunOnboardingAsync();
            }
        }
        catch (Exception ex)
        {
            // Startup failing must not take the process down: the tray icon stays, so the user can
            // reach settings, see the message and download whatever is missing.
            _overlay?.Show(new OverlayStatus(OverlayState.Error, DescribeStartupFailure(ex)));

            // The overlay message is deliberately short and non-technical, and for a year it was
            // the only thing Jane said about a failed start -- while itself pointing at a log that
            // did not exist. The exception goes here, where it can be read afterwards.
            StartupLog().Write(LogLevel.Error, "startup", "The dictation graph did not start.",
                LogFields.New()
                    .Add("exception", ex.GetType().FullName)
                    .Add("detail", ex.Message)
                    .Add("inner", ex.InnerException?.Message)
                    .Add("stack", ex.StackTrace));
        }
    }

    /// <summary>
    /// A log to write to when the host is the thing that failed.
    /// </summary>
    /// <remarks>
    /// <see cref="JaneHost.Create"/> opens its own and hands it to everything it builds, but the
    /// failure being reported here may be that very call throwing, in which case there is no host
    /// and no log to reach through it. Opening a second one against the same directory is safe:
    /// <see cref="FileLog"/> holds no handle between writes.
    /// </remarks>
    private FileLog StartupLog() => _host?.Log ?? new FileLog(new JanePaths().Logs);

    /// <summary>
    /// Shows the user the folder Jane logs into, creating it first so the window is never empty.
    /// </summary>
    /// <remarks>
    /// Explorer is asked to open the directory rather than to select the file, because a rolled
    /// log leaves several and the newest is not always the one worth reading. Nothing here can
    /// fail loudly: if Explorer is unavailable, the menu item having done nothing visible is a
    /// better outcome than an unhandled exception on the UI thread.
    /// </remarks>
    private void OpenLogFolder()
    {
        var directory = StartupLog().Directory;

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            using var explorer = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLog().Write(LogLevel.Warning, "tray", "Could not open the log folder.",
                LogFields.New().Add("path", directory).Add("detail", ex.Message));
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

        // Last: the name has to outlive everything it protects, or a launch during teardown gets
        // in and starts hooking the keyboard while this copy is still unhooking it.
        _instance?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Another copy of Jane tried to start. Show the settings window so the launch did something.
    /// </summary>
    /// <remarks>
    /// Raised on the guard's watcher thread, so it marshals. Settings rather than a message box:
    /// somebody launching Jane when it is already running is usually looking for it, and the
    /// settings window is the thing they were looking for.
    /// </remarks>
    private void OnSecondInstanceAttempted(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _windows?.ShowSettings());

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

            case TrayCommand.OpenLogs:
                OpenLogFolder();
                break;

            default:
                // Dictate and Autostart need nothing here: the tray settles autostart itself, and
                // "dictate" is the hotkey, which is always listening.
                break;
        }
    }
}
