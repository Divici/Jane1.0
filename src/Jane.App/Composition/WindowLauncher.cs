using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Jane.App.History;
using Jane.App.Onboarding;
using Jane.App.Settings;
using Jane.Core.Abstractions;
using Jane.Core.History;

namespace Jane.App.Composition;

/// <summary>
/// Owns the settings, history and onboarding windows: creates each at most once, and brings an
/// existing one forward instead of opening a second.
/// </summary>
/// <remarks>
/// Jane is a tray app with no main window, so nothing else would stop a user clicking "Settings"
/// four times and getting four windows over the same database. Each is also closed rather than
/// disposed on quit, because they hold no resources of their own -- everything they show belongs
/// to <see cref="JaneHost"/>.
/// </remarks>
public sealed class WindowLauncher(JaneHost host)
{
    private SettingsWindow? _settings;
    private HistoryWindow? _history;

    public void ShowSettings()
    {
        if (BringForward(_settings))
        {
            return;
        }

        _settings = new SettingsWindow(
            host.Settings2,
            host.Dictionary,
            host.Instructions,
            host.Provisioner,
            host.Microphones,
            host.Blocklist,
            visible => host.SetOverlayVisible(visible),
            host.Database);

        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    public void ShowHistory()
    {
        if (BringForward(_history))
        {
            return;
        }

        _history = new HistoryWindow(
            host.History,
            host.GetForegroundWindow,

            // Re-inject goes through the same injector and the same identity check as a live
            // dictation, so a stale or dead target aborts exactly as it would then.
            entry => _ = host.ReinjectAsync(entry, CancellationToken.None),
            copyToClipboard: null,
            host.Database);

        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }

    /// <summary>Shows onboarding and returns once the user has finished or closed it.</summary>
    public async Task<bool> RunOnboardingAsync()
    {
        var window = new FirstRunWindow(
            host.Settings2,
            host.Provisioner,
            host.Microphones,
            host.MicrophoneCheck,
            host.TestDictationAsync);

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completed.TrySetResult(host.Settings.OnboardingComplete);

        window.Show();
        return await completed.Task;
    }

    private static bool BringForward(Window? window)
    {
        if (window is null)
        {
            return false;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        return true;
    }
}
