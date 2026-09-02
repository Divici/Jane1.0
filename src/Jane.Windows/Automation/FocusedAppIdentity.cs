using System.Runtime.InteropServices;
using Jane.Core.Abstractions;

namespace Jane.Windows.Automation;

/// <summary>Whether a window handle still refers to a live window.</summary>
/// <remarks>
/// Split out from <see cref="IFocusTracker"/> because "the app you were dictating into closed"
/// and "you switched to something else" are different messages to put in front of the user, and
/// the foreground window alone cannot tell them apart.
/// </remarks>
public interface IWindowLiveness
{
    bool IsAlive(nint handle);
}

/// <summary>
/// Names the window that has focus, and says whether a previously named one still exists.
/// </summary>
/// <remarks>
/// <para>
/// Captured at key-down and re-read immediately before injection. The pairing of handle with
/// process id and name is the point: window handles are recycled, so a stale handle can match a
/// window in a completely different application, and typing a dictated sentence into it would
/// be a data leak rather than a typo.
/// </para>
/// <para>
/// The process name comes from <c>QueryFullProcessImageName</c> rather than
/// <c>Process.GetProcessById</c>: it needs only PROCESS_QUERY_LIMITED_INFORMATION, which is
/// granted for protected and higher-integrity processes where opening a fuller handle is
/// refused. A window Jane cannot name is still a window Jane must not type into blindly, so an
/// unreadable name degrades to empty rather than throwing.
/// </para>
/// </remarks>
public sealed unsafe partial class FocusedAppIdentity : IFocusTracker, IWindowLiveness
{
    private const int MaxClassName = 256;
    private const int MaxPathLength = 512;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public TargetWindow GetForegroundWindow()
    {
        var handle = GetForegroundWindowNative();
        return handle == 0 ? TargetWindow.None : Describe(handle);
    }

    /// <summary>Identity of a specific window, for history re-inject against a recorded handle.</summary>
    public TargetWindow Describe(nint handle)
    {
        if (!IsAlive(handle))
        {
            return TargetWindow.None;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);

        Span<char> buffer = stackalloc char[MaxPathLength];
        string windowClass;
        string windowTitle;
        fixed (char* pinned = buffer)
        {
            windowClass = Read(GetClassName(handle, pinned, MaxClassName), buffer);
            windowTitle = Read(GetWindowText(handle, pinned, MaxPathLength), buffer);
        }

        return new TargetWindow(
            Handle: handle,
            ProcessId: (int)processId,
            ProcessName: ProcessNameOf(processId),
            WindowClass: windowClass,
            WindowTitle: windowTitle);

        static string Read(int length, Span<char> buffer) =>
            length <= 0 ? string.Empty : new string(buffer[..Math.Min(length, buffer.Length)]);
    }

    public bool IsAlive(nint handle) => handle != 0 && IsWindow(handle);

    /// <summary>Lower-case, extension stripped -- the key the strategy table and settings use.</summary>
    private static string ProcessNameOf(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, 0, processId);
        if (process == 0)
        {
            return string.Empty;
        }

        try
        {
            Span<char> buffer = stackalloc char[MaxPathLength];
            var length = MaxPathLength;
            fixed (char* pinned = buffer)
            {
                if (QueryFullProcessImageName(process, 0, pinned, ref length) == 0 || length <= 0)
                {
                    return string.Empty;
                }
            }

            var path = new string(buffer[..Math.Min(length, MaxPathLength)]);
            return Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint GetForegroundWindowNative();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint handle);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint handle, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassName(nint handle, char* buffer, int capacity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowText(nint handle, char* buffer, int capacity);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    private static partial int QueryFullProcessImageName(nint process, uint flags, char* buffer, ref int capacity);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
