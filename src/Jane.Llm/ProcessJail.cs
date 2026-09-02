using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jane.Llm;

/// <summary>
/// A Windows Job Object with <c>KILL_ON_JOB_CLOSE</c>: every process assigned to it dies when
/// this object is disposed, or when Jane's process exits for any reason -- including a crash,
/// a kill from Task Manager, or a debugger stop.
/// </summary>
/// <remarks>
/// Without this, a supervised <c>ollama.exe</c> outlives a Jane crash, keeps holding VRAM, and
/// there is nothing left running to unload it. That is the exact resource leak the user's hard
/// constraint forbids, and the one shape a `finally` block cannot cover.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ProcessJail : IDisposable
{
    private nint _handle;
    private bool _disposed;

    public ProcessJail(string name)
    {
        // An unnamed job avoids colliding with a stale job from a previous crashed run; the
        // name is kept for diagnostics only.
        Name = name;
        _handle = CreateJobObjectW(nint.Zero, null);
        if (_handle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = nint.Zero;
                throw new InvalidOperationException($"SetInformationJobObject failed with Win32 error {error}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string Name { get; }

    public bool Assign(nint processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AssignProcessToJobObject(_handle, processHandle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != nint.Zero)
        {
            // Closing the last handle to the job is what kills the children.
            CloseHandle(_handle);
            _handle = nint.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
