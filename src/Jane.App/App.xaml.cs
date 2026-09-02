using System.Windows;
using Jane.App.Overlay;
using Jane.App.Tray;
using Jane.Core.Abstractions;

namespace Jane.App;

/// <summary>
/// Jane's process lifetime: a tray icon and a floating pill, and nothing else on screen.
/// </summary>
/// <remarks>
/// This is deliberately not a composition root. Phase 5 introduces
/// <c>Composition/ServiceRegistration</c>, which owns the hotkey, capture, recognition and
/// injection graph and subscribes to <see cref="ITrayCommands.CommandInvoked"/> to give the
/// remaining menu items their behaviour. Until then the tray raises every command and only
/// Quit -- the one command that is purely about this class's own lifetime -- is answered here.
/// A menu wired to stubs would be worse than one wired to nothing: it would look finished.
/// </remarks>
public partial class App : Application
{
    private TrayIcon? _tray;
    private OverlayPresenter? _overlay;

    /// <summary>The tray's command surface, for the Phase 5 composition root to subscribe to.</summary>
    public ITrayCommands? Tray => _tray;

    public IOverlayPresenter? Overlay => _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The overlay's HWND is created here rather than on first dictation: building a window
        // costs a few milliseconds, and the hotkey's budget from key-down to armed is 50 ms.
        _overlay = new OverlayPresenter(Dispatcher);

        _tray = new TrayIcon();
        _tray.CommandInvoked += OnTrayCommand;
        _tray.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _overlay?.Dispose();

        base.OnExit(e);
    }

    private void OnTrayCommand(object? sender, TrayCommandEventArgs e)
    {
        if (e.Command == TrayCommand.Quit)
        {
            Shutdown();
        }
    }
}
