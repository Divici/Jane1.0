using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using Jane.Core.Diagnostics;

namespace Jane.Windows.Diagnostics;

/// <summary>
/// Reports who, if anyone, is listening on Ollama's default port.
/// </summary>
/// <remarks>
/// Jane deliberately runs a supervised standalone <c>ollama.exe</c> on a non-default port. The
/// risk this probe covers is the winget desktop package: it installs a tray service that owns
/// :11434, autostarts, holds VRAM while you game, and auto-updates over the network. Jane would
/// never talk to it, but its presence quietly breaks both the zero-VRAM-when-idle promise and
/// the zero-egress one, so the user should know it is there.
/// </remarks>
public sealed class PortOwnerProbe(int port = 11434) : IProbe
{
    public string Name => "ollama.default_port";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == port)
            .ToArray();

        var data = new Dictionary<string, string>
        {
            ["port"] = port.ToString(CultureInfo.InvariantCulture),
            ["listener_count"] = listeners.Length.ToString(CultureInfo.InvariantCulture),
        };

        if (listeners.Length == 0)
        {
            return Task.FromResult(new ProbeResult(
                Name, ProbeStatus.Pass, $"Nothing is listening on :{port}.", Data: data));
        }

        // .NET has no managed owner lookup for a TCP listener, and the P/Invoke to
        // GetExtendedTcpTable is a lot of surface for a diagnostic. Naming the likely culprit
        // is enough: the remedy is the same whichever process it turns out to be.
        var suspects = Process.GetProcessesByName("ollama")
            .Concat(Process.GetProcessesByName("ollama app"))
            .ToArray();
        try
        {
            var names = suspects.Length > 0
                ? string.Join(", ", suspects.Select(p => $"{p.ProcessName} (pid {p.Id})"))
                : "an unidentified process";
            data["suspects"] = names;

            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Warn,
                $"Something is listening on :{port} -- {names}. Jane does not use this server.",
                Remedy: "This is most likely the Ollama desktop app. Jane supervises its own standalone ollama.exe on :11435 and ignores :11434, but the desktop app autostarts, can hold VRAM while you game, and auto-updates over the network. Uninstall it (winget uninstall Ollama.Ollama) if you want Jane's resource and zero-egress guarantees to hold machine-wide.",
                Data: data));
        }
        finally
        {
            foreach (var process in suspects)
            {
                process.Dispose();
            }
        }
    }
}
