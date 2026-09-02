using System.Globalization;
using Jane.Core.Diagnostics;
using NAudio.CoreAudioApi;

namespace Jane.Windows.Diagnostics;

/// <summary>Confirms there is a capture device Jane can open, and names the default one.</summary>
/// <remarks>
/// Enumerates only -- it does not open a stream. Opening here would take the device for the
/// duration of the probe and leave the mic-in-use indicator flickering, and the real capture
/// path (Phase 2) opens at app start and stays open anyway.
/// </remarks>
public sealed class MicrophoneProbe : IProbe
{
    public string Name => "audio.microphone";

    public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();

        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        if (devices.Count == 0)
        {
            return Task.FromResult(new ProbeResult(
                Name,
                ProbeStatus.Fail,
                "No active audio capture device found.",
                Remedy: "Plug in a microphone and enable it in Settings > System > Sound > Input. Jane cannot dictate without one."));
        }

        string? defaultName = null;
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            using var @default = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultName = @default.FriendlyName;
            defaultId = @default.ID;
        }

        var names = new List<string>(devices.Count);
        foreach (var device in devices)
        {
            names.Add(device.FriendlyName);
            device.Dispose();
        }

        var data = new Dictionary<string, string>
        {
            ["device_count"] = devices.Count.ToString(CultureInfo.InvariantCulture),
            ["devices"] = string.Join(" | ", names),
        };
        if (defaultName is not null)
        {
            data["default"] = defaultName;
            data["default_id"] = defaultId!;
        }

        return Task.FromResult(new ProbeResult(
            Name,
            ProbeStatus.Pass,
            defaultName is null
                ? $"{devices.Count} capture device(s) found, but Windows has no default communications input set."
                : $"{devices.Count} capture device(s); default is \"{defaultName}\".",
            Remedy: defaultName is null
                ? "Set a default input device in Settings > System > Sound > Input, or pick one explicitly in Jane's settings."
                : null,
            Data: data));
    }
}
