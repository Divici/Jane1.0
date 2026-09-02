using System.Runtime.InteropServices;
using System.Text;

namespace Jane.Windows.Gpu;

/// <summary>Live NVIDIA GPU telemetry, read straight from <c>nvml.dll</c>.</summary>
/// <remarks>
/// NVML ships with the display driver -- no CUDA toolkit is required, which matters because
/// this machine has none. Every entry point is resolved lazily and every failure degrades to
/// "no GPU information", never an exception: Jane runs ASR on the CPU and simply skips the LLM
/// when the GPU is unreadable, so an absent NVML is a warning, not a fault.
/// </remarks>
public sealed partial class NvmlInterop : IDisposable
{
    // The driver installs nvml.dll into System32; older layouts only had it under Program Files.
    private const string Nvml = "nvml.dll";

    private bool _initialised;
    private bool _disposed;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return NativeLibrary.TryLoad(Nvml, out var handle) && Free(handle);
            }
            catch (DllNotFoundException)
            {
                return false;
            }

            static bool Free(nint handle)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
    }

    /// <summary>Returns false when NVML is missing or refuses to initialise.</summary>
    public bool TryInitialise(out string? error)
    {
        error = null;
        if (_initialised)
        {
            return true;
        }

        try
        {
            var rc = nvmlInit_v2();
            if (rc != NvmlReturn.Success)
            {
                error = $"nvmlInit_v2 returned {rc}.";
                return false;
            }

            _initialised = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "nvml.dll not found. It ships with the NVIDIA display driver.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"nvml.dll is present but missing an entry point: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<GpuSnapshot> Snapshot()
    {
        if (!_initialised)
        {
            return [];
        }

        if (nvmlDeviceGetCount_v2(out var count) != NvmlReturn.Success || count == 0)
        {
            return [];
        }

        var snapshots = new List<GpuSnapshot>(count);
        for (uint i = 0; i < count; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != NvmlReturn.Success)
            {
                continue;
            }

            var nameBuffer = new byte[96];
            var name = nvmlDeviceGetName(device, nameBuffer, (uint)nameBuffer.Length) == NvmlReturn.Success
                ? Encoding.UTF8.GetString(nameBuffer).TrimEnd('\0')
                : $"GPU {i}";

            // Memory and utilisation are read independently: a driver that answers one and not
            // the other still gives a usable partial answer for the governor.
            var memory = nvmlDeviceGetMemoryInfo(device, out var mem) == NvmlReturn.Success
                ? mem
                : default;
            var utilisation = nvmlDeviceGetUtilizationRates(device, out var util) == NvmlReturn.Success
                ? util
                : default;

            snapshots.Add(new GpuSnapshot(
                Index: i,
                Name: name,
                TotalBytes: memory.Total,
                FreeBytes: memory.Free,
                UsedBytes: memory.Used,
                GpuUtilisationPercent: utilisation.Gpu,
                MemoryUtilisationPercent: utilisation.Memory));
        }

        return snapshots;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialised)
        {
            try
            {
                _ = nvmlShutdown();
            }
            catch (DllNotFoundException)
            {
                // The library vanished under us (driver update mid-session). Nothing to release.
            }

            _initialised = false;
        }
    }

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlInit_v2();

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlShutdown();

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlDeviceGetCount_v2(out int deviceCount);

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlDeviceGetName(nint device, [Out] byte[] name, uint length);

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);

    [LibraryImport(Nvml)]
    private static partial NvmlReturn nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private enum NvmlReturn
    {
        Success = 0,
        Uninitialized = 1,
        InvalidArgument = 2,
        NotSupported = 3,
        NoPermission = 4,
        InsufficientSize = 7,
        DriverNotLoaded = 9,
        Unknown = 999,
    }
}

/// <param name="FreeBytes">What the GPU governor routes on: free VRAM is the resource Jane must not take.</param>
public sealed record GpuSnapshot(
    uint Index,
    string Name,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    uint GpuUtilisationPercent,
    uint MemoryUtilisationPercent);
