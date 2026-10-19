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
/// The on-demand microphone rests on a claim: readying a capture device is slow and starting an
/// already-readied one is fast, so moving the first half to launch takes it off the key-down path.
/// That is true of WASAPI in general and is not equally true of every driver, and a fix nobody can
/// check is a fix nobody should believe. So the halves are timed separately here and every number
/// goes in the report.
/// </para>
/// <para>
/// The first version of this probe timed the start <em>call</em>, and passed a device that was
/// clipping the first half-second of every cold dictation. On a Jabra Link 380 the call returns in
/// 10 ms and the stream then delivers nothing but digital zeros for another 360 to 570 ms while the
/// dongle brings the headset's link up. The number that matters is how long after the key press
/// the first buffer with sound in it arrives, so that is what is waited for and judged now.
/// </para>
/// <para>
/// This probe does briefly activate the endpoint -- there is no way to measure a start without
/// starting. It runs only when the user asks for a diagnostic, it stops within a couple of
/// seconds, and the report says so. A Bluetooth headset may audibly switch profile once while it
/// runs; that is the cost of measuring rather than assuming.
/// </para>
/// </remarks>
public sealed class MicrophoneLatencyProbe(ICaptureDeviceFactory? devices = null, string? deviceId = null) : IProbe
{
    private readonly ICaptureDeviceFactory _devices = devices ?? new WasapiDeviceFactory();

    /// <summary>Above this, the start call itself is slow enough that a fast speaker loses a syllable.</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Above this, the device is awake late enough that the first word of a cold dictation is
    /// clipped. A wired microphone delivers sound in its first buffer, 20 to 40 ms after the start.
    /// </summary>
    private static readonly TimeSpan FirstSoundBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>How long to wait for sound before concluding the device is not going to deliver any.</summary>
    private static readonly TimeSpan FirstSoundTimeout = TimeSpan.FromMilliseconds(1_500);

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
                ["first_sound_ms"] = first.FirstSound is { } sound ? Milliseconds(sound) : "none",
                ["second_open_ms"] = Milliseconds(second.Open),
                ["second_start_ms"] = Milliseconds(second.Start),
                ["second_first_sound_ms"] = second.FirstSound is { } secondSound ? Milliseconds(secondSound) : "none",
                ["keydown_budget_ms"] = Milliseconds(StartBudget),
                ["first_sound_budget_ms"] = Milliseconds(FirstSoundBudget),
            };

            var readying = $"\"{second.Device}\": readying costs {Milliseconds(second.Open + second.Prepare)} ms and is paid off the key-down path.";

            if (second.FirstSound is not { } firstSound)
            {
                return new ProbeResult(
                    Name,
                    ProbeStatus.Warn,
                    readying + $" After a key press the start call returned in {Milliseconds(second.Start)} ms, but the device delivered no sound within {Milliseconds(FirstSoundTimeout)} ms of starting.",
                    Remedy: "Check the microphone is not muted -- on the device, in Settings > System > Sound > Input, and in any noise-suppression app -- and that Windows has the right one selected as the default communications device.",
                    Data: data);
            }

            var detail = readying +
                $" After a key press the first sound arrives {Milliseconds(firstSound)} ms later (the start call itself returned in {Milliseconds(second.Start)} ms), measured on a rebuilt device rather than a fresh one.";

            if (second.Start <= StartBudget && firstSound <= FirstSoundBudget)
            {
                return new ProbeResult(Name, ProbeStatus.Pass, detail, Data: data);
            }

            return new ProbeResult(
                Name,
                ProbeStatus.Warn,
                detail + $" That is over the {Milliseconds(FirstSoundBudget)} ms budget: anything said before the device is awake is never recorded, so with the microphone set to open only while dictating the first word of a dictation can be clipped.",
                Remedy: "Keep the microphone setting on \"All the time\", the default, which removes the wait entirely. \"Only while I'm dictating\" is for a Bluetooth headset that must drop back to stereo between dictations, and the pill then says Connecting until the device is actually hearing you.",
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

    /// <param name="FirstSound">
    /// From the start call to the first buffer with a non-zero sample in it. Null when none
    /// arrived inside <see cref="FirstSoundTimeout"/>.
    /// </param>
    private readonly record struct Cycle(string Device, TimeSpan Open, TimeSpan Prepare, TimeSpan Start, TimeSpan? FirstSound);

    /// <summary>
    /// Opens, readies, activates, listens for the first sound, and releases the device once --
    /// exactly as a cold dictation does.
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

            // Subscribed before the start, because a wired device delivers sound in its very
            // first buffer and that buffer can arrive before the start call has returned.
            var startedAt = 0L;
            var heard = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnSamples(object? sender, ReadOnlyMemory<float> samples)
            {
                if (heard.Task.IsCompleted)
                {
                    return;
                }

                foreach (var sample in samples.Span)
                {
                    if (sample != 0f)
                    {
                        heard.TrySetResult(Stopwatch.GetElapsedTime(startedAt));
                        return;
                    }
                }
            }

            stream.SamplesAvailable += OnSamples;
            try
            {
                startedAt = Stopwatch.GetTimestamp();
                stream.Start();
                var start = Stopwatch.GetElapsedTime(startedAt);

                TimeSpan? firstSound;
                try
                {
                    firstSound = await heard.Task.WaitAsync(FirstSoundTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    firstSound = null;
                }

                return new Cycle(stream.DeviceName, open, prepare, start, firstSound);
            }
            finally
            {
                stream.SamplesAvailable -= OnSamples;
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static string Milliseconds(TimeSpan span) =>
        span.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
