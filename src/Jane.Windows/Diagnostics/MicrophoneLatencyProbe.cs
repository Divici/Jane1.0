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
        try
        {
            // Two cycles, because the bug this probe failed to catch only appeared on the second
            // one. A probe that opens, starts and stops once tests the launch path and nothing
            // else -- and the launch path was never the broken one.
            var first = await MeasureCycleAsync(cancellationToken).ConfigureAwait(false);
            var second = await MeasureCycleAsync(cancellationToken).ConfigureAwait(false);

            var data = new Dictionary<string, string>
            {
                ["device"] = second.Device,
                ["open_ms"] = Milliseconds(first.Open),
                ["prepare_ms"] = Milliseconds(first.Prepare),
                ["start_ms"] = Milliseconds(first.Start),
                ["second_open_ms"] = Milliseconds(second.Open),
                ["second_start_ms"] = Milliseconds(second.Start),
                ["keydown_budget_ms"] = Milliseconds(StartBudget),
            };

            var detail =
                $"\"{second.Device}\": readying costs {Milliseconds(second.Open + second.Prepare)} ms and is paid off the key-down path; a key press costs {Milliseconds(second.Start)} ms, measured on a rebuilt device rather than a fresh one.";

            return second.Start <= StartBudget
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A start that refuses is the failure mode this probe exists to surface, and it must
            // be reported rather than thrown out of a diagnostic run.
            return new ProbeResult(
                Name,
                ProbeStatus.Fail,
                $"The microphone could not be driven through a second open-and-start cycle: {ex.GetType().Name}: {ex.Message}",
                Remedy: "This is the shape of a driver Jane cannot rebuild between dictations. Report it with this line; dictation will fail after the first idle release.");
        }
    }

    private readonly record struct Cycle(string Device, TimeSpan Open, TimeSpan Prepare, TimeSpan Start);

    /// <summary>
    /// Opens, readies, activates and releases the device once, exactly as a dictation does.
    /// </summary>
    /// <remarks>
    /// Disposing rather than stopping is not a shortcut: a capture stream is single-use, so
    /// releasing it is the only way to make the endpoint inactive, and rebuilding is what
    /// <see cref="Audio.WasapiCapture"/> itself does at the end of every idle window.
    /// </remarks>
    private async Task<Cycle> MeasureCycleAsync(CancellationToken cancellationToken)
    {
        var openedAt = Stopwatch.GetTimestamp();
        var stream = await _devices.OpenAsync(deviceId, cancellationToken).ConfigureAwait(false);

        try
        {
            var open = Stopwatch.GetElapsedTime(openedAt);

            var preparedAt = Stopwatch.GetTimestamp();
            stream.Prepare();
            var prepare = Stopwatch.GetElapsedTime(preparedAt);

            var startedAt = Stopwatch.GetTimestamp();
            stream.Start();
            var start = Stopwatch.GetElapsedTime(startedAt);

            return new Cycle(stream.DeviceName, open, prepare, start);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static string Milliseconds(TimeSpan span) =>
        span.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
