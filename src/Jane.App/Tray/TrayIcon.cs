using System.IO;
using System.Security;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Jane.Core.Abstractions;

// Imported by name: System.Drawing's Brushes, Point and Size all collide with WPF's.
using Icon = System.Drawing.Icon;

namespace Jane.App.Tray;

/// <summary>
/// Jane's notification-area icon and its menu.
/// </summary>
/// <remarks>
/// This is the whole of Jane's chrome. There is no main window: a dictation tool that owns a
/// window would spend its life minimised, and a window that can be focused is a window that can
/// swallow the hotkey. Everything the user can reach without speaking is here.
/// <para>
/// The icon is not registered with the shell until <see cref="Show"/>, so composing the app --
/// or constructing one of these in a test -- never flashes an icon into the user's tray.
/// </para>
/// </remarks>
public sealed class TrayIcon : ITrayCommands, IDisposable
{
    private const string Tooltip = "Jane - hold the hotkey to dictate";

    private readonly AutostartRegistration _autostart;
    private readonly Dictionary<TrayCommand, MenuItem> _items = [];
    private readonly Icon _artwork;
    private readonly TaskbarIcon _icon;

    private HotkeyMode _mode = HotkeyMode.Hold;
    private bool _paused;
    private bool _disposed;

    public TrayIcon(AutostartRegistration? autostart = null)
    {
        _autostart = autostart ?? new AutostartRegistration();
        _artwork = TrayIconArtwork.Create();

        Menu = BuildMenu();

        _icon = new TaskbarIcon
        {
            Icon = _artwork,
            ToolTipText = Tooltip,
            ContextMenu = Menu,

            // Left click opens the menu too. A tray-only app with a right-click-only menu is a
            // feature most users never find.
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };

        Mode = _mode;
        _items[TrayCommand.Autostart].IsChecked = ReadAutostart();
    }

    public event EventHandler<TrayCommandEventArgs>? CommandInvoked;

    /// <summary>The menu itself, so the composition root can attach to it if it needs to.</summary>
    public ContextMenu Menu { get; }

    /// <summary>True once the icon is registered with the shell.</summary>
    public bool IsShown => _icon.IsCreated;

    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            _items[TrayCommand.Pause].IsChecked = value;
            _icon.ToolTipText = value ? "Jane - paused" : Tooltip;
        }
    }

    public HotkeyMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _items[TrayCommand.ModeHoldToTalk].IsChecked = value == HotkeyMode.Hold;
            _items[TrayCommand.ModeToggle].IsChecked = value == HotkeyMode.Toggle;
        }
    }

    /// <summary>The menu item behind a command. Exposed so state can be inspected and asserted.</summary>
    public MenuItem ItemFor(TrayCommand command) => _items[command];

    /// <summary>
    /// Runs a command exactly as clicking its menu item would.
    /// </summary>
    /// <remarks>
    /// Checkable state is settled here, before the event goes out, so a subscriber always sees a
    /// tray whose check marks already match what it is being told.
    /// </remarks>
    public void Invoke(TrayCommand command)
    {
        var state = command switch
        {
            TrayCommand.Pause => TogglePause(),
            TrayCommand.ModeHoldToTalk => SelectMode(HotkeyMode.Hold),
            TrayCommand.ModeToggle => SelectMode(HotkeyMode.Toggle),
            TrayCommand.Autostart => ToggleAutostart(),
            _ => false,
        };

        CommandInvoked?.Invoke(this, new TrayCommandEventArgs(command, state));
    }

    /// <summary>Registers the icon with the shell.</summary>
    public void Show()
    {
        if (!_disposed && !_icon.IsCreated)
        {
            // Never with efficiency mode: it puts the whole process into EcoQoS, and Jane runs
            // its speech recognition on the CPU in this process. Throttling that would trade a
            // few milliwatts at idle for seconds of latency on every dictation.
            _icon.ForceCreate(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Dispose();
        _artwork.Dispose();
    }

    private static MenuItem Item(string header) => new() { Header = header };

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Register(TrayCommand.Dictate, "_Start dictation"));
        menu.Items.Add(new Separator());

        var mode = Item("_Mode");
        mode.Items.Add(Register(TrayCommand.ModeHoldToTalk, "_Hold to talk"));
        mode.Items.Add(Register(TrayCommand.ModeToggle, "_Toggle"));
        menu.Items.Add(mode);

        menu.Items.Add(Register(TrayCommand.Pause, "_Pause Jane"));
        menu.Items.Add(new Separator());

        menu.Items.Add(Register(TrayCommand.Settings, "Se_ttings..."));
        menu.Items.Add(Register(TrayCommand.History, "_History..."));
        menu.Items.Add(Register(TrayCommand.Autostart, "Start with _Windows"));
        menu.Items.Add(new Separator());

        menu.Items.Add(Register(TrayCommand.Quit, "_Quit Jane"));

        return menu;
    }

    private MenuItem Register(TrayCommand command, string header)
    {
        var item = Item(header);

        // IsCheckable stays off: the item's check mark tracks Jane's actual state, which the
        // handler decides, not the click. A self-toggling item would show "paused" for the
        // instant between the click and a handler that refused.
        item.Click += (_, _) => Invoke(command);

        _items[command] = item;
        return item;
    }

    private bool TogglePause()
    {
        IsPaused = !IsPaused;
        return IsPaused;
    }

    private bool SelectMode(HotkeyMode mode)
    {
        Mode = mode;
        return true;
    }

    private bool ToggleAutostart()
    {
        var wanted = !ReadAutostart();

        try
        {
            _autostart.Set(wanted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // A managed or locked-down profile can refuse the write. Falling through to a
            // re-read leaves the tick showing what the registry actually says.
        }

        var actual = ReadAutostart();
        _items[TrayCommand.Autostart].IsChecked = actual;

        return actual;
    }

    private bool ReadAutostart()
    {
        try
        {
            return _autostart.IsEnabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
