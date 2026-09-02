using Jane.Core.Platform;

namespace Jane.Core.Diagnostics.Probes;

/// <summary>Confirms the model directory's drive has room for the weights Jane downloads.</summary>
public sealed class DiskSpaceProbe(JanePaths paths, long requiredBytes = DiskSpaceProbe.DefaultRequiredBytes) : IProbe
{
    /// <summary>
    /// Parakeet int8 is ~640 MB, its archive another ~640 MB during extraction, Silero VAD is
    /// small, and a whisper.cpp GGUF is up to ~1.6 GB if `bench` picks that engine. 6 GB leaves
    /// room for all of it plus the temp copies, without demanding an unreasonable amount.
    /// </summary>
    public const long DefaultRequiredBytes = 6L * 1024 * 1024 * 1024;

    public string Name => "storage.disk_free";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(paths.Models));
        if (string.IsNullOrEmpty(root))
        {
            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Fail, $"Cannot determine the drive holding {paths.Models}.",
                Remedy: $"Set {JanePaths.EnvModelDir} to an absolute path on a local drive."));
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Fail, $"Drive {root} is not ready.",
                Remedy: $"Connect {root}, or point {JanePaths.EnvModelDir} at a local drive."));
        }

        var free = drive.AvailableFreeSpace;
        var freeGb = free / (double)(1024 * 1024 * 1024);
        var needGb = requiredBytes / (double)(1024 * 1024 * 1024);
        var data = new Dictionary<string, string>
        {
            ["drive"] = root,
            ["free_bytes"] = free.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["required_bytes"] = requiredBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return Task.FromResult(free >= requiredBytes
            ? new ProbeResult(Name, ProbeStatus.Pass, $"{freeGb:F1} GB free on {root} (need {needGb:F1} GB).", Data: data)
            : new ProbeResult(Name, ProbeStatus.Fail, $"Only {freeGb:F1} GB free on {root}; models need {needGb:F1} GB.",
                Remedy: $"Free up space on {root}, or point {JanePaths.EnvModelDir} at a drive that has room.", Data: data));
    }
}
