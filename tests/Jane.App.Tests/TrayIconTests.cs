using System.Globalization;
using System.Windows.Controls;
using Jane.App.Tray;
using Jane.Core.Abstractions;
using Microsoft.Win32;

namespace Jane.App.Tests;

/// <summary>
/// The tray menu is Jane's only chrome -- there is no main window -- so a menu item that
/// silently does nothing is a feature the user cannot reach at all.
/// </summary>
public sealed class TrayIconTests
{
    [Fact]
    public void EveryMenuItemDispatchesItsOwnCommand()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            using var tray = new TrayIcon();
            var received = new List<TrayCommand>();
            tray.CommandInvoked += (_, e) => received.Add(e.Command);

            foreach (var command in Enum.GetValues<TrayCommand>())
            {
                tray.ItemFor(command).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            }

            Assert.Equal(Enum.GetValues<TrayCommand>(), received);
        });
    }

    [Fact]
    public void EveryMenuItemIsLabelledAndKeyboardReachable()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            using var tray = new TrayIcon();

            foreach (var command in Enum.GetValues<TrayCommand>())
            {
                var item = tray.ItemFor(command);

                // A blank header is unreachable by keyboard and unreadable by a screen reader.
                var header = Assert.IsType<string>(item.Header);
                Assert.False(string.IsNullOrWhiteSpace(header), $"{command} has no label.");

                // The underscore is WPF's access-key marker: without one the item can only be
                // reached with arrow keys, which is a step down for a keyboard-driven app.
                Assert.Contains("_", header, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void PauseTogglesAndReportsTheNewStateWithTheCommand()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            using var tray = new TrayIcon();
            var states = new List<bool>();
            tray.CommandInvoked += (_, e) =>
            {
                if (e.Command == TrayCommand.Pause)
                {
                    states.Add(e.IsChecked);
                }
            };

            Assert.False(tray.IsPaused);

            tray.Invoke(TrayCommand.Pause);
            Assert.True(tray.IsPaused);
            Assert.True(tray.ItemFor(TrayCommand.Pause).IsChecked);

            tray.Invoke(TrayCommand.Pause);
            Assert.False(tray.IsPaused);
            Assert.False(tray.ItemFor(TrayCommand.Pause).IsChecked);

            Assert.Equal([true, false], states);
        });
    }

    [Fact]
    public void ModeSelectionShowsWhichModeIsActive()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            using var tray = new TrayIcon();

            // Hold-to-talk is the default, and the menu has to say so without being opened twice.
            Assert.Equal(HotkeyMode.Hold, tray.Mode);
            Assert.True(tray.ItemFor(TrayCommand.ModeHoldToTalk).IsChecked);
            Assert.False(tray.ItemFor(TrayCommand.ModeToggle).IsChecked);

            tray.Invoke(TrayCommand.ModeToggle);

            Assert.Equal(HotkeyMode.Toggle, tray.Mode);
            Assert.False(tray.ItemFor(TrayCommand.ModeHoldToTalk).IsChecked);
            Assert.True(tray.ItemFor(TrayCommand.ModeToggle).IsChecked);

            // The composition root owns the setting, so pushing state in must move the menu too.
            tray.Mode = HotkeyMode.Hold;
            Assert.True(tray.ItemFor(TrayCommand.ModeHoldToTalk).IsChecked);
        });
    }

    [Fact]
    public void ConstructingTheTrayIconPutsNothingInTheNotificationArea()
    {
        using var sta = new StaTestContext();

        sta.Invoke(() =>
        {
            // Creation is deferred to Show() so composing the app -- or running these tests --
            // does not flash an icon into the user's tray.
            using var tray = new TrayIcon();

            Assert.False(tray.IsShown);
        });
    }

    [Fact]
    public void AutostartTargetsTheCurrentUsersRunKey()
    {
        // HKCU, never HKLM: HKLM needs elevation, and a dictation tool that demands admin to tick
        // a checkbox is a far worse trade than the convenience is worth.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutostartRegistration.RunKeyPath);
        Assert.Equal("Jane", AutostartRegistration.DefaultValueName);
    }

    [Fact]
    public void AutostartWritesAndRemovesItsRunValue()
    {
        var keyPath = @"Software\Jane\Tests\" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var autostart = new AutostartRegistration(keyPath, "Jane", @"C:\Program Files\Jane\Jane.exe");

        try
        {
            Assert.False(autostart.IsEnabled);

            autostart.Set(true);
            Assert.True(autostart.IsEnabled);

            // Quoted: %ProgramFiles% contains a space, and an unquoted Run value would launch
            // "C:\Program" with "Files\Jane\Jane.exe" as an argument.
            Assert.Equal("\"C:\\Program Files\\Jane\\Jane.exe\"", autostart.RegisteredCommand);

            autostart.Set(false);
            Assert.False(autostart.IsEnabled);
            Assert.Null(autostart.RegisteredCommand);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Jane\Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void AutostartMenuItemReflectsAndFlipsTheRegisteredValue()
    {
        using var sta = new StaTestContext();
        var keyPath = @"Software\Jane\Tests\" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        try
        {
            sta.Invoke(() =>
            {
                using var tray = new TrayIcon(new AutostartRegistration(keyPath, "Jane", @"C:\Jane\Jane.exe"));

                Assert.False(tray.ItemFor(TrayCommand.Autostart).IsChecked);

                tray.Invoke(TrayCommand.Autostart);
                Assert.True(tray.ItemFor(TrayCommand.Autostart).IsChecked);

                tray.Invoke(TrayCommand.Autostart);
                Assert.False(tray.ItemFor(TrayCommand.Autostart).IsChecked);
            });
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Jane\Tests", throwOnMissingSubKey: false);
        }
    }
}
