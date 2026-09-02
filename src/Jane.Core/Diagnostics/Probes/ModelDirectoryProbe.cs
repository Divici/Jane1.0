using Jane.Core.Platform;

namespace Jane.Core.Diagnostics.Probes;

/// <summary>Confirms Jane can actually write where it intends to put model weights.</summary>
/// <remarks>
/// Checks by writing, not by inspecting an ACL. A directory can look writable and not be --
/// controlled-folder-access, a read-only network path, or a full disk all present as an
/// ordinary directory until the first write fails, which would otherwise happen halfway
/// through a 622 MB download.
/// </remarks>
public sealed class ModelDirectoryProbe(JanePaths paths) : IProbe
{
    public string Name => "storage.model_dir";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var dir = paths.Models;

        try
        {
            Directory.CreateDirectory(dir);

            var canary = Path.Combine(dir, $".jane-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(canary, "jane");
            File.Delete(canary);

            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Pass,
                $"{dir} exists and is writable.",
                Data: new Dictionary<string, string> { ["path"] = dir }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Fail,
                $"Cannot write to {dir}: {ex.Message}",
                Remedy: $"Grant write access to {dir}, or point {JanePaths.EnvModelDir} at a writable directory.",
                Data: new Dictionary<string, string> { ["path"] = dir }));
        }
    }
}
