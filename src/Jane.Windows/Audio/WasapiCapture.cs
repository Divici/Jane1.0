using System.Buffers;
using Jane.Core.Abstractions;

namespace Jane.Windows.Audio;

/// <summary>
/// Jane's microphone: armed on key-down, and open either permanently or only while dictating.
/// </summary>
/// <remarks>
/// Four rules live here and nowhere else.
/// <list type="bullet">
/// <item><see cref="MicrophoneActivation"/> decides when the device <em>captures</em>. The default
/// starts on key-down and releases a few seconds after the dictation ends, because a running
/// capture stream forces a Bluetooth headset into its narrowband call profile and quietly ruins
/// every other sound on the machine. Readying the device is separate and happens at launch, and
/// again immediately after each release: it is silent, and paying for it there rather than on
/// key-down is what stopped the first word of every dictation going missing.</item>
/// <item>A capture stream is single-use, so "let go of the microphone" means release and rebuild
/// rather than stop and reuse. Stopping and keeping the client is what made every dictation after
/// the first report "No microphone" -- see <see cref="ReleaseAndReady"/>.</item>
/// <item>Arming back-dates the buffer by whatever pre-roll exists, so the first word survives the
/// tens of milliseconds between "the user started talking" and "the key registered". A released
/// stream has no pre-roll to back-date and drops what it held, so audio from before the release
/// can never be spliced onto the front of the next dictation.</item>
/// <item>A capture is capped in length and a lost device is reconnected, both without the app
/// restarting and without the caller having to poll anything.</item>
/// <item>Everything is swappable at runtime through <see cref="Reconfigure"/>: choosing a
/// different microphone in settings must not need a restart.</item>
/// </list>
/// The class is named for WASAPI because that is what it is in production, but it holds no Win32
/// itself -- <see cref="ICaptureDeviceFactory"/> is the only door to the hardware.
/// </remarks>
public sealed class WasapiCapture : IAudioSource
{
    private readonly ICaptureDeviceFactory _devices;
    private readonly PreRollBuffer _preRoll;
    private readonly ArrayBufferWriter<float> _captured;
    private readonly int _maxCaptureSamples;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Lock _captureGate = new();

    /// <summary>
    /// Fires once, <see cref="AudioCaptureOptions.IdleRelease"/> after a dictation ends.
    /// </summary>
    /// <remarks>
    /// A timer rather than a delay task because it is rescheduled on every dictation, and a
    /// cancelled-and-recreated task per key press is a lot of garbage for something that usually
    /// does nothing.
    /// </remarks>
    private readonly Timer _idleRelease;

    private AudioCaptureOptions _options;
    private ICaptureStream? _stream;
    private CancellationTokenSource? _reconnect;
    private AudioSourceState _state = new(IsOpen: false, IsCapturing: false, DeviceName: null, Error: null);

    private Task _armed = Task.CompletedTask;

    /// <summary>Whether the endpoint is activated. Distinct from the client merely existing.</summary>
    private bool _running;

    /// <summary>Starts refused since the last key-down. Bounds the rebuild-and-retry to one.</summary>
    private int _startAttempts;
    private bool _isCapturing;
    private int _preRollSamples;
    private CaptureStopReason? _latchedStop;
    private float _level;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public WasapiCapture(ICaptureDeviceFactory devices, AudioCaptureOptions? options = null)
    {
        _devices = devices;
        _options = options ?? new AudioCaptureOptions();
        _preRoll = PreRollBuffer.ForWindow(_options.PreRoll);
        _maxCaptureSamples = AudioFormat.SamplesFor(_options.MaxCaptureDuration);
        _idleRelease = new Timer(_ => ReleaseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);

        // One dictation's worth of headroom, so the steady state never resizes mid-capture.
        _captured = new ArrayBufferWriter<float>(_preRoll.Capacity + AudioFormat.SampleRate * 30);
    }

    /// <summary>Convenience for composition: the real WASAPI factory with default options.</summary>
    public static WasapiCapture ForDefaultDevice(AudioCaptureOptions? options = null) =>
        new(new WasapiDeviceFactory(), options);

    public AudioSourceState State => Volatile.Read(ref _state);

    public event EventHandler<AudioSourceState>? StateChanged;

    /// <summary>
    /// The capture ended without anyone asking -- the duration cap tripped, or the device went
    /// away. Phase 5 subscribes and calls <see cref="Stop"/> to collect the buffer; the reason it
    /// passes is overridden by the latched one, so a late or wrong caller cannot lose the cause.
    /// </summary>
    public event EventHandler<CaptureStopReason>? AutoStopped;

    /// <summary>The options currently in force, after any <see cref="Reconfigure"/>.</summary>
    public AudioCaptureOptions Options => Volatile.Read(ref _options);

    /// <summary>
    /// Completes when the device the last <see cref="Arm"/> asked for is live, or has failed.
    /// </summary>
    /// <remarks>
    /// Under <see cref="MicrophoneActivation.WhileDictating"/> arming starts a device open and
    /// returns without waiting -- the alternative is blocking the hotkey pump on a driver, which
    /// on a Bluetooth headset can mean the better part of a second. This is how a caller that
    /// does care, such as a test or a diagnostic, finds out when the microphone is actually
    /// live. It never faults: an open that fails is reported through <see cref="State"/>.
    /// </remarks>
    public Task Armed => Volatile.Read(ref _armed);

    /// <summary>
    /// Readies the microphone at startup, and under <see cref="MicrophoneActivation.AlwaysOpen"/>
    /// starts it as well.
    /// </summary>
    /// <remarks>
    /// Under the default this prepares the audio client and stops there. The endpoint stays
    /// inactive -- no samples, no capture indicator, a Bluetooth headset still in stereo -- and
    /// the several hundred milliseconds of driver work that used to land on key-down is paid here
    /// instead, while the tray icon is appearing. A failure is reported through
    /// <see cref="State"/> rather than thrown: a microphone that could not be readied must not
    /// stop Jane starting, and the next key press retries.
    /// </remarks>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Options.Activation == MicrophoneActivation.AlwaysOpen)
        {
            await OpenGuardedAsync(start: true, rethrow: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var readying = OpenGuardedAsync(start: false, rethrow: false, cancellationToken);
        Volatile.Write(ref _armed, readying);
        await readying.ConfigureAwait(false);
    }

    public void Arm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A dictation starting inside the grace window keeps the device it already has, and gets
        // its pre-roll back as a side effect.
        _idleRelease.Change(Timeout.Infinite, Timeout.Infinite);

        // Each key press gets its own rebuild budget: a device that failed an hour ago must not
        // make this press give up without trying.
        _startAttempts = 0;

        lock (_captureGate)
        {
            _captured.ResetWrittenCount();
            _preRollSamples = _preRoll.CopyTo(_captured.GetSpan(_preRoll.Capacity));
            _captured.Advance(_preRollSamples);
            _latchedStop = null;
            _startedAt = DateTimeOffset.UtcNow;
            _isCapturing = true;
        }

        Publish(_state with { IsCapturing = true });

        var stream = _stream;
        if (stream is null)
        {
            // Fire and forget on purpose: this is called from the hotkey pump, which must not
            // wait on a driver. Samples are retained from the moment the device comes up.
            Volatile.Write(ref _armed, OpenGuardedAsync(start: true, rethrow: false, CancellationToken.None));
            return;
        }

        // The prepared-client path, and the reason the first word survives. Starting an already
        // initialised stream is a single call into the audio engine rather than the endpoint
        // activation and format negotiation that used to happen here.
        StartStream(stream);
        Volatile.Write(ref _armed, Task.CompletedTask);
    }

    /// <summary>
    /// Activates a prepared stream, and rebuilds it rather than failing if it will not start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ring was emptied when the previous stream was released, so there is no stale audio to
    /// back-date.
    /// </para>
    /// <para>
    /// The fallback exists because a start that throws used to escape <see cref="Arm"/>, reach the
    /// orchestrator, and be reported as "No microphone" -- for every dictation from then on, since
    /// nothing replaced the unusable stream. A driver quirk should cost one device open, not every
    /// dictation until the app is restarted, so a refusal here discards the stream and opens a new
    /// one on the same path a cold key-down takes.
    /// </para>
    /// </remarks>
    private void StartStream(ICaptureStream stream)
    {
        if (_running)
        {
            return;
        }

        try
        {
            stream.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseStream();
            _running = false;

            // Exactly one rebuild per key press. Retrying without a ceiling would spin open-and-
            // fail against a device that is simply unavailable -- a busy loop on the audio engine
            // where the honest answer is a message. The next key-down is the next attempt, which
            // is a retry the user asked for rather than one Jane inflicted.
            if (_startAttempts++ == 0 && !_disposed)
            {
                Publish(_state with { IsOpen = false, Error = null });
                Volatile.Write(ref _armed, OpenGuardedAsync(start: true, rethrow: false, CancellationToken.None));
                return;
            }

            Publish(_state with { IsOpen = false, Error = DescribeStartFailure(ex) });
            return;
        }

        _startAttempts = 0;
        _running = true;
        Publish(_state with { IsOpen = true, Error = null });
    }

    /// <summary>
    /// Names the failure in the user's own terms, keeping the driver's words.
    /// </summary>
    /// <remarks>
    /// The message matters: "The audio client is already initialized" is what would have named
    /// this bug in one line, and it was thrown away in favour of a generic sentence about checking
    /// Windows sound settings.
    /// </remarks>
    private static string DescribeStartFailure(Exception ex) =>
        $"The microphone would not start ({ex.GetType().Name}: {ex.Message}). Jane is opening it again.";

    /// <summary>
    /// Changes which microphone is used and when it is held open.
    /// </summary>
    /// <remarks>
    /// Applied to the next dictation rather than to one in flight: pulling the device out from
    /// under a capture would lose the words already spoken into it, and nobody changes their
    /// microphone mid-sentence on purpose.
    /// </remarks>
    public void Reconfigure(MicrophoneRouting routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var previous = Options;
        var updated = previous.With(routing);

        // Every setter in the settings window writes the whole object, so this is called when
        // something entirely unrelated changes. Without this guard, adjusting the LLM's context
        // size would let go of a microphone that is sitting warm inside its grace window and make
        // the next dictation a cold open for no reason.
        if (updated == previous)
        {
            return;
        }

        Volatile.Write(ref _options, updated);

        if (Volatile.Read(ref _isCapturing))
        {
            return;
        }

        if (routing.Activation == MicrophoneActivation.AlwaysOpen)
        {
            if (previous.DeviceId != routing.DeviceId)
            {
                Release();
            }

            _ = OpenGuardedAsync(start: true, rethrow: false, CancellationToken.None);
            return;
        }

        // Switching to on-demand, or pointing at a different endpoint: let go of whatever is
        // open so the next dictation opens the right device -- and so a headset the user just
        // stopped using goes back to stereo now rather than at the next key press.
        Release();

        // Then ready the new one, still stopped. Without this, changing microphone in settings
        // would make the next dictation a cold open and lose its first word -- the exact bug this
        // mode was reworked to fix, reintroduced through the one path that discards the client.
        Volatile.Write(ref _armed, OpenGuardedAsync(start: false, rethrow: false, CancellationToken.None));
    }

    public CapturedAudio Stop(CaptureStopReason reason)
    {
        float[] samples;
        int preRollSamples;
        DateTimeOffset startedAt;
        CaptureStopReason? latched;

        lock (_captureGate)
        {
            samples = _captured.WrittenSpan.ToArray();
            preRollSamples = _preRollSamples;

            // A stop with no matching arm -- the key was already down when Jane started -- has
            // no start time of its own; "now" beats a default DateTimeOffset in the history.
            startedAt = _startedAt == default ? DateTimeOffset.UtcNow : _startedAt;
            latched = _latchedStop;

            _captured.ResetWrittenCount();
            _preRollSamples = 0;
            _latchedStop = null;
            _isCapturing = false;
        }

        Publish(_state with { IsCapturing = false });
        ScheduleIdleRelease();

        // A caller saying "released" has no opinion; anything else -- cancelled, too short -- is
        // a deliberate verdict about this dictation and outranks the latch.
        var effective = latched is not null && reason == CaptureStopReason.Released ? latched.Value : reason;
        return new CapturedAudio(samples, preRollSamples, effective, startedAt);
    }

    private void ScheduleIdleRelease()
    {
        if (_disposed || Options.Activation == MicrophoneActivation.AlwaysOpen)
        {
            return;
        }

        var delay = Options.IdleRelease;
        if (delay <= TimeSpan.Zero)
        {
            ReleaseIfIdle();
            return;
        }

        _idleRelease.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Lets go of the device, unless a dictation started while the grace window was running.
    /// </summary>
    /// <remarks>
    /// The re-check is the whole point. The release is scheduled when a dictation ends and fires
    /// several seconds later; by then the user may well be halfway through the next one, and
    /// closing the stream underneath it would silently truncate what they said.
    /// </remarks>
    private void ReleaseIfIdle()
    {
        lock (_captureGate)
        {
            if (_isCapturing)
            {
                return;
            }
        }

        if (Options.Activation == MicrophoneActivation.AlwaysOpen)
        {
            return;
        }

        ReleaseAndReady();
    }

    /// <summary>
    /// Lets go of the recorder and immediately readies a fresh one, still stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the idle-release timer does. Releasing is the part a Bluetooth headset reacts
    /// to and the part that puts out the capture indicator, so the user-visible promise -- the
    /// microphone is off between dictations -- is kept in full.
    /// </para>
    /// <para>
    /// It has to be a release and a rebuild rather than a stop, because a recorder that has been
    /// started cannot be started again: NAudio's <c>StopRecording</c> leaves the
    /// <c>IAudioClient</c> initialised and its <c>StartRecording</c> initialises unconditionally,
    /// so the second start fails with "The audio client is already initialized". Stopping and
    /// keeping the client was the previous design, and it is what made every dictation after the
    /// first report "No microphone" until Jane was restarted.
    /// </para>
    /// <para>
    /// The rebuild runs here, at the end of the idle window, rather than on the next key-down --
    /// so the device open is still off the path the user feels, which was the whole point of
    /// readying the microphone early in the first place.
    /// </para>
    /// </remarks>
    private void ReleaseAndReady()
    {
        _idleRelease.Change(Timeout.Infinite, Timeout.Infinite);

        if (_stream is null)
        {
            return;
        }

        CloseStream();
        _running = false;

        // Emptied here rather than at the next start, so there is no window in which the ring
        // holds audio from before the release. Back-dating that onto the next dictation would
        // splice two unrelated moments into one utterance.
        _preRoll.Clear();
        Volatile.Write(ref _level, 0f);
        Publish(_state with { IsOpen = false, Error = null });

        if (_disposed)
        {
            return;
        }

        // Fire and forget, and recorded in Armed so a test or a diagnostic can wait for it. A
        // key-down that arrives mid-rebuild finds _stream null and goes through OpenGuardedAsync,
        // which serialises on the same gate.
        Volatile.Write(ref _armed, OpenGuardedAsync(start: false, rethrow: false, CancellationToken.None));
    }

    /// <summary>Closes the stream and forgets the audio in the ring, without an error state.</summary>
    /// <remarks>
    /// The ring has to be cleared. Retaining it would let audio captured minutes ago, before the
    /// device was released, arrive as the pre-roll of the next dictation -- a stranger bug to
    /// diagnose than an empty pre-roll, and a worse one to have in a transcript.
    /// </remarks>
    private void Release()
    {
        _idleRelease.Change(Timeout.Infinite, Timeout.Infinite);

        if (_stream is null)
        {
            return;
        }

        CloseStream();
        _running = false;
        _preRoll.Clear();
        Volatile.Write(ref _level, 0f);
        Publish(_state with { IsOpen = false, Error = null });
    }

    /// <summary>Readies the device if it is not already, optionally starting it, publishing any failure.</summary>
    private async Task OpenGuardedAsync(bool start, bool rethrow, CancellationToken cancellationToken)
    {
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_stream is { } existing)
            {
                if (start)
                {
                    StartStream(existing);
                }

                return;
            }

            await OpenCoreAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (AudioDeviceException error)
        {
            Publish(_state with { IsOpen = false, Error = error.Message });
            StartReconnectLoop();

            if (rethrow)
            {
                throw;
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _idleRelease.DisposeAsync().ConfigureAwait(false);

        var reconnect = Interlocked.Exchange(ref _reconnect, null);
        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
            reconnect.Dispose();
        }

        CloseStream();
        Publish(new AudioSourceState(IsOpen: false, IsCapturing: false, _state.DeviceName, _state.Error));
        _openGate.Dispose();
    }

    private async Task OpenCoreAsync(bool start, CancellationToken cancellationToken)
    {
        var stream = await _devices.OpenAsync(_options.DeviceId, cancellationToken).ConfigureAwait(false);
        stream.SamplesAvailable += OnSamplesAvailable;
        stream.Stopped += OnStopped;

        // Always prepared, only sometimes started. Preparing is the slow, silent half; starting is
        // the cheap half that a headset and a capture indicator can both notice.
        stream.Prepare();

        _stream = stream;
        _running = false;
        Publish(new AudioSourceState(
            IsOpen: false, IsCapturing: _state.IsCapturing, stream.DeviceName, Error: null));

        if (start)
        {
            StartStream(stream);
        }
    }

    private void OnSamplesAvailable(object? sender, ReadOnlyMemory<float> samples)
    {
        // Runs on the device callback thread. The pre-roll is filled unconditionally -- that is
        // what makes arming free -- and only the retained-capture branch takes any decision.
        var span = samples.Span;
        _preRoll.Write(span);
        UpdateLevel(span);

        CaptureStopReason? autoStop = null;
        lock (_captureGate)
        {
            if (_isCapturing)
            {
                var budget = _preRollSamples + _maxCaptureSamples - _captured.WrittenCount;
                if (span.Length < budget)
                {
                    _captured.Write(span);
                }
                else
                {
                    _captured.Write(span[..budget]);
                    _latchedStop = CaptureStopReason.MaxDurationReached;
                    _isCapturing = false;
                    autoStop = CaptureStopReason.MaxDurationReached;
                }
            }
        }

        if (autoStop is not null)
        {
            Publish(_state with { IsCapturing = false });
            AutoStopped?.Invoke(this, autoStop.Value);
        }
    }

    /// <summary>
    /// Recent loudness in [0, 1], for the overlay waveform.
    /// </summary>
    /// <remarks>
    /// Computed on the device callback thread as a decayed peak, which costs one pass over a
    /// buffer that was already in cache. A separate meter reading the samples again would double
    /// the memory traffic on the one thread that must never fall behind.
    /// </remarks>
    public float CurrentLevel => Volatile.Read(ref _level);

    private void UpdateLevel(ReadOnlySpan<float> span)
    {
        var peak = 0f;
        foreach (var sample in span)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        // Rise instantly, fall slowly: a meter that tracked the decay symmetrically would flicker
        // through every glottal stop and read as noise rather than as speech.
        var previous = Volatile.Read(ref _level);
        Volatile.Write(ref _level, peak > previous ? peak : (previous * 0.85f) + (peak * 0.15f));
    }

    private void OnStopped(object? sender, AudioDeviceException? error)
    {
        var message = error?.Message ?? "The capture device stopped unexpectedly.";
        var wasCapturing = false;

        lock (_captureGate)
        {
            if (_isCapturing)
            {
                _latchedStop = CaptureStopReason.DeviceLost;
                _isCapturing = false;
                wasCapturing = true;
            }
        }

        CloseStream();

        // Audio from the old endpoint must never lead a capture from its replacement: two mics
        // at two gains spliced into one utterance is a bizarre thing to debug.
        _preRoll.Clear();
        Publish(new AudioSourceState(IsOpen: false, IsCapturing: false, _state.DeviceName, message));

        if (wasCapturing)
        {
            AutoStopped?.Invoke(this, CaptureStopReason.DeviceLost);
        }

        StartReconnectLoop();
    }

    private void CloseStream()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        stream.SamplesAvailable -= OnSamplesAvailable;
        stream.Stopped -= OnStopped;
        stream.Dispose();
    }

    private void StartReconnectLoop()
    {
        // Never under WhileDictating: a background retry loop would reopen the microphone Jane
        // just deliberately let go of, which is exactly the state this mode exists to avoid. The
        // next key press retries, which is the only moment the device is wanted anyway.
        if (!_options.AutoReconnect || _disposed ||
            Options.Activation != MicrophoneActivation.AlwaysOpen)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _reconnect, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => ReconnectAsync(cts, cts.Token), CancellationToken.None);
    }

    private async Task ReconnectAsync(CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.ReconnectInterval, cancellationToken).ConfigureAwait(false);

                await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_stream is not null || _disposed)
                    {
                        return;
                    }

                    // The reconnect loop only runs under AlwaysOpen, where the device is meant to be live.
                    await OpenCoreAsync(start: true, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (AudioDeviceException)
                {
                    // Still unplugged. The state already says so; keep retrying quietly rather
                    // than filling the log with one line per interval.
                }
                finally
                {
                    _openGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Disposed mid-retry. Nothing to recover to.
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _reconnect, null, owner) == owner)
            {
                owner.Dispose();
            }
        }
    }

    private void Publish(AudioSourceState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, state);
    }
}
