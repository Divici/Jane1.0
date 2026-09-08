using System.Diagnostics;
using System.Globalization;
using Jane.Core.Diagnostics;
using Jane.Windows.Audio;

namespace Jane.Windows.Diagnostics;

/// <summary>
/// Measures what a key press actually costs on this machine's microphone.
/// </summary>
/// <remarks>
/// <para>
/// The clipped-first-word fix rests on a claim: readying a capture device is slow and starting an
/// already-readied one is fast, so moving the first half to launch takes it off the key-down path.
/// That is true of WASAPI in general and is not equally true of every driver, and a fix nobody can
/// check is a fix nobody should believe. So the two halves are timed separately here and both
/// numbers go in the report.
/// </para>
/// <para>
/// This probe does briefly activate the endpoint -- there is no way to measure a start without
/// starting. It runs only when the user asks for a diagnostic, it stops within milliseconds, and
/// the report says so. A Bluetooth headset may audibly switch profile once while it runs; that is
/// the cost of measuring rather than assuming.
/// </para>
/// </remarks>
public sealed class MicrophoneLatencyProbe(ICaptureDeviceFactory? devices = null, string? deviceId = null) : IProbe
{
    private readonly ICaptureDeviceFactory _devices = devices ?? new WasapiDeviceFactory();

    /// <summary>Above this, a key press is late enough that a fast speaker loses a syllable.</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromMilliseconds(50);

    public string Name => "audio.microphone_latency";

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        ICaptureStream? stream = null;

        try
        {
            var openedAt = Stopwatch.GetTimestamp();
            stream = await _devices.OpenAsync(deviceId, cancellationToken).ConfigureAwait(false);
            var open = Stopwatch.GetElapsedTime(openedAt);

            var preparedAt = Stopwatch.GetTimestamp();
            stream.Prepare();
            var prepare = Stopwatch.GetElapsedTime(preparedAt);

            var startedAt = Stopwatch.GetTimestamp();
            stream.Start();
            var start = Stopwatch.GetElapsedTime(startedAt);

            var stoppedAt = Stopwatch.GetTimestamp();
            stream.Stop();
            var stop = Stopwatch.GetElapsedTime(stoppedAt);

            var data = new Dictionary<string, string>
            {
                ["device"] = stream.DeviceName,
                ["open_ms"] = Milliseconds(open),
                ["prepare_ms"] = Milliseconds(prepare),
                ["start_ms"] = Milliseconds(start),
                ["stop_ms"] = Milliseconds(stop),
                ["keydown_budget_ms"] = Milliseconds(StartBudget),
            };

            var detail =
                $"\"{stream.DeviceName}\": readying costs {Milliseconds(open + prepare)} ms and is paid at launch; a key press costs {Milliseconds(start)} ms.";

            return start <= StartBudget
                ? new ProbeResult(Name, ProbeStatus.Pass, detail, Data: data)
                : new ProbeResult(
                    Name,
                    ProbeStatus.Warn,
                    detail + $" That is over the {Milliseconds(StartBudget)} ms budget, so the first word of a dictation can still be clipped.",
                    Remedy: "Try a different microphone, or switch the microphone setting to \"All the time\" -- which removes the start cost entirely, at the price of holding a Bluetooth headset in its narrowband call profile.",
                    Data: data);
        }
        catch (AudioDeviceException ex)
        {
            return new ProbeResult(
                Name,
                ProbeStatus.Warn,
                $"The capture device could not be opened, so its latency is unknown: {ex.Message}",
                Remedy: "Check that a microphone is present and not held exclusively by another application.");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string Milliseconds(TimeSpan span) =>
        span.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
