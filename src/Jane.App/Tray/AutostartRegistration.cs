using Microsoft.Win32;

namespace Jane.App.Tray;

/// <summary>
/// Jane's "start with Windows" toggle, backed by the per-user <c>Run</c> key.
/// </summary>
/// <remarks>
/// HKCU rather than HKLM, and a registry value rather than a scheduled task or a service: HKLM
/// and Task Scheduler both need elevation, and a dictation tool that asks for admin to tick a
/// checkbox is a far worse trade than the convenience is worth. The <c>Run</c> key also starts
/// Jane <em>after</em> the shell, which is what a tray app wants.
/// <para>
/// Every path is injectable so tests write to a scratch key instead of the user's real one.
/// </para>
/// </remarks>
public sealed class AutostartRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string DefaultValueName = "Jane";

    private readonly string _keyPath;
    private readonly string _valueName;

    public AutostartRegistration(string? keyPath = null, string? valueName = null, string? executablePath = null)
    {
        _keyPath = keyPath ?? RunKeyPath;
        _valueName = valueName ?? DefaultValueName;

        var executable = executablePath
            ?? Environment.ProcessPath
            ?? Environment.GetCommandLineArgs()[0];

        Command = Quote(executable);
    }

    /// <summary>What would be written -- the executable path, quoted.</summary>
    public string Command { get; }

    /// <summary>What is written right now, or null if autostart is off.</summary>
    public string? RegisteredCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(_valueName) as string;
        }
    }

    public bool IsEnabled => !string.IsNullOrEmpty(RegisteredCommand);

    public void Set(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(_valueName, Command, RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        existing?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    // %ProgramFiles% contains a space, and the shell splits an unquoted Run value on it -- an
    // unquoted entry would try to launch "C:\Program" with the rest as arguments.
    private static string Quote(string path) =>
        path.StartsWith('"') ? path : string.Concat("\"", path, "\"");
}
