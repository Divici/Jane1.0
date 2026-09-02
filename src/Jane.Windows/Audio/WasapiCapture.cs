using System.Buffers;
using Jane.Core.Abstractions;

namespace Jane.Windows.Audio;

/// <summary>
/// Jane's microphone: opened once at app start, held open, and armed on key-down.
/// </summary>
/// <remarks>
/// Three rules live here and nowhere else.
/// <list type="bullet">
/// <item>The device is opened at startup, so a key press never waits on a driver. The cost is a
/// permanent mic-in-use indicator, which onboarding discloses.</item>
/// <item>Arming back-dates the buffer by the pre-roll window, so the first word survives the
/// tens of milliseconds between "the user started talking" and "the key registered".</item>
/// <item>A capture is capped in length and a lost device is reconnected, both without the app
/// restarting and without the caller having to poll anything.</item>
/// </list>
/// The class is named for WASAPI because that is what it is in production, but it holds no Win32
/// itself -- <see cref="ICaptureDeviceFactory"/> is the only door to the hardware.
/// </remarks>
public sealed class WasapiCapture : IAudioSource
{
    private readonly ICaptureDeviceFactory _devices;
    private readonly AudioCaptureOptions _options;
    private readonly PreRollBuffer _preRoll;
    private readonly ArrayBufferWriter<float> _captured;
    private readonly int _maxCaptureSamples;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Lock _captureGate = new();

    private ICaptureStream? _stream;
    private CancellationTokenSource? _reconnect;
    private AudioSourceState _state = new(IsOpen: false, IsCapturing: false, DeviceName: null, Error: null);

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

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                return;
            }

            await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AudioDeviceException error)
        {
            Publish(_state with { IsOpen = false, Error = error.Message });
            StartReconnectLoop();
            throw;
        }
        finally
        {
            _openGate.Release();
        }
    }

    public void Arm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

        // A caller saying "released" has no opinion; anything else -- cancelled, too short -- is
        // a deliberate verdict about this dictation and outranks the latch.
        var effective = latched is not null && reason == CaptureStopReason.Released ? latched.Value : reason;
        return new CapturedAudio(samples, preRollSamples, effective, startedAt);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        var stream = await _devices.OpenAsync(_options.DeviceId, cancellationToken).ConfigureAwait(false);
        stream.SamplesAvailable += OnSamplesAvailable;
        stream.Stopped += OnStopped;
        stream.Start();

        _stream = stream;
        Publish(new AudioSourceState(IsOpen: true, IsCapturing: false, stream.DeviceName, Error: null));
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
        if (!_options.AutoReconnect || _disposed)
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

                    await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
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
