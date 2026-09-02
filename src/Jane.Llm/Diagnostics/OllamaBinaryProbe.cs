using System.Diagnostics;
using Jane.Core.Diagnostics;

namespace Jane.Llm.Diagnostics;

/// <summary>Confirms the standalone <c>ollama.exe</c> Jane supervises is on disk and runnable.</summary>
public sealed class OllamaBinaryProbe(string exePath) : IProbe
{
    public string Name => "ollama.binary";

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string> { ["path"] = exePath };

        if (!File.Exists(exePath))
        {
            return new ProbeResult(
                Name, ProbeStatus.Fail, $"{exePath} does not exist.",
                Remedy: "Run build/get-ollama.ps1. Jane deliberately uses the standalone release archive rather than the winget desktop package, which would own :11434, autostart and auto-update over the network.",
                Data: data);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            // `ollama --version` prints a "could not connect" warning to stdout when no server is
            // up; the client version line is still there and is the part that matters here.
            var line = stdout.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Contains("version is", StringComparison.OrdinalIgnoreCase))
                ?? stdout.Trim();

            data["version_output"] = line;
            var size = new FileInfo(exePath).Length;
            data["size_bytes"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return new ProbeResult(Name, ProbeStatus.Pass, $"Standalone ollama.exe present: {line}", Data: data);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProbeResult(
                Name, ProbeStatus.Fail, $"{exePath} exists but could not be executed: {ex.Message}",
                Remedy: "Re-run build/get-ollama.ps1 -Force to replace a corrupt extraction.",
                Data: data);
        }
    }
}
