using Jane.Core.Abstractions;

namespace Jane.App.Tray;

/// <summary>Everything the tray menu can ask for.</summary>
public enum TrayCommand
{
    /// <summary>Start a dictation without touching the hotkey.</summary>
    Dictate,

    ModeHoldToTalk,

    ModeToggle,

    Settings,

    History,

    /// <summary>Suspend the hotkey without quitting -- for a game that binds the same key.</summary>
    Pause,

    /// <summary>Launch with Windows. Handled inside the tray, since it is pure registry state.</summary>
    Autostart,

    Quit,
}

/// <param name="Command">Which item was chosen.</param>
/// <param name="IsChecked">
/// The new state of a checkable item -- Pause, Autostart, and the two mode items. False for the
/// rest, which carry no state of their own.
/// </param>
public sealed record TrayCommandEventArgs(TrayCommand Command, bool IsChecked = false);

/// <summary>
/// The tray's command surface.
/// </summary>
/// <remarks>
/// Every menu item raises <see cref="CommandInvoked"/>; none of them reaches into the app. That
/// is deliberate rather than incidental -- Settings and History have no windows yet, and a menu
/// item wired to a stub is a button that lies. Phase 5's composition root subscribes here and
/// decides what each command means, so the tray stays a view with no dependencies of its own.
/// <para>
/// <see cref="IsPaused"/> and <see cref="Mode"/> are settable so that state changed anywhere else
/// -- the hotkey, settings, a restored session -- shows up as a check mark without the tray
/// needing to know where it came from.
/// </para>
/// </remarks>
public interface ITrayCommands
{
    event EventHandler<TrayCommandEventArgs>? CommandInvoked;

    bool IsPaused { get; set; }

    HotkeyMode Mode { get; set; }
}
