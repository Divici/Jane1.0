using NAudio.CoreAudioApi;

namespace Jane.App.Settings;

/// <param name="Id">
/// A WASAPI endpoint id, or <c>null</c> for "whatever Windows calls the default communications
/// input". Null is a real choice rather than an absent one: it is what makes swapping headsets
/// re-route without Jane being told, which <see cref="Jane.Windows.Audio.WasapiDeviceFactory"/>
/// implements with NAudio's automatic stream routing.
/// </param>
public sealed record MicrophoneChoice(string? Id, string Name, bool IsDefault = false)
{
    /// <summary>What the combo box shows.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Lists the capture endpoints the settings window offers.
/// </summary>
/// <remarks>
/// An interface because the real implementation talks to the audio stack, and a settings window
/// that cannot be opened on a machine with no microphone is a settings window nobody can use to
/// fix having no microphone.
/// </remarks>
public interface IMicrophoneCatalog
{
    /// <summary>Active capture devices, default first. Never throws; an empty list is the failure.</summary>
    IReadOnlyList<MicrophoneChoice> List();
}

/// <summary>
/// The real device list, read from WASAPI.
/// </summary>
/// <remarks>
/// Enumerates only. Opening each endpoint to confirm it works would take the microphone away
/// from whatever call the user is on and leave the in-use indicator flickering down the list --
/// <see cref="Jane.Windows.Diagnostics.MicrophoneProbe"/> makes the same choice for the same
/// reason.
/// </remarks>
public sealed class WasapiMicrophoneCatalog : IMicrophoneCatalog
{
    public IReadOnlyList<MicrophoneChoice> List()
    {
        var choices = new List<MicrophoneChoice>
        {
            new(null, "Windows default (Communications)", IsDefault: true),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    choices.Add(new MicrophoneChoice(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A broken audio stack must not stop the settings window opening. The window shows
            // its designed "no microphones" state, which is also the truthful one here.
        }

        return choices;
    }
}
