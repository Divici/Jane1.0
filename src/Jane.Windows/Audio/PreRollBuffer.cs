using Jane.Core.Abstractions;

namespace Jane.Windows.Audio;

/// <summary>
/// A fixed-size ring of the most recent audio, kept filled whenever the device is open.
/// </summary>
/// <remarks>
/// A key press is registered tens of milliseconds after the user has begun speaking -- the
/// switch travel, the hook queue and the pipeline all add latency in the same direction -- so a
/// capture that starts at key-down clips the first word. Arming back-dates the buffer by this
/// window instead, which costs 32 KB of RAM and nothing on the hot path.
/// <para>
/// The device callback writes and the arming thread reads, so both take the same lock. Capture
/// callbacks arrive every ~10 ms and arming happens once per dictation, so contention is
/// effectively zero -- and this is not the keyboard hook proc, where a lock would be forbidden.
/// </para>
/// </remarks>
public sealed class PreRollBuffer
{
    /// <summary>
    /// One second.
    /// </summary>
    /// <remarks>
    /// Was 500 ms, chosen to cover the gap between "the user started talking" and "the key
    /// registered". A second also covers the case that produced the clipped-first-word report:
    /// somebody who begins speaking as they press, on a machine where the stream takes a moment
    /// to deliver its first buffer. It costs 16k floats -- 64 KB -- and nothing on the hot path,
    /// and the leading silence it adds is what the voice-activity gate trims for a living.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly float[] _ring;
    private int _writeIndex;
    private int _count;

    public PreRollBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, 1);
        _ring = new float[capacitySamples];
    }

    public static PreRollBuffer ForWindow(TimeSpan window) =>
        new(AudioFormat.SamplesFor(window));

    public int Capacity => _ring.Length;

    /// <summary>Samples currently retained -- below <see cref="Capacity"/> only just after a
    /// device open or a <see cref="Clear"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends a device callback's worth of samples, discarding whatever falls out of
    /// the window.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            // A callback larger than the whole ring can only leave its tail behind, so skip
            // straight to that tail rather than looping over samples that are already dead.
            if (samples.Length >= _ring.Length)
            {
                samples[^_ring.Length..].CopyTo(_ring);
                _writeIndex = 0;
                _count = _ring.Length;
                return;
            }

            var untilWrap = _ring.Length - _writeIndex;
            if (samples.Length <= untilWrap)
            {
                samples.CopyTo(_ring.AsSpan(_writeIndex));
            }
            else
            {
                samples[..untilWrap].CopyTo(_ring.AsSpan(_writeIndex));
                samples[untilWrap..].CopyTo(_ring);
            }

            _writeIndex = (_writeIndex + samples.Length) % _ring.Length;
            _count = Math.Min(_ring.Length, _count + samples.Length);
        }
    }

    /// <summary>
    /// Copies the most recent samples into <paramref name="destination"/>, oldest first.
    /// </summary>
    /// <returns>How many samples were written -- the smaller of <see cref="Count"/> and the
    /// destination length.</returns>
    /// <remarks>
    /// A destination shorter than <see cref="Count"/> gets the audio *nearest* the key press,
    /// not the oldest in the ring: a shorter configured pre-roll must mean less lead-in, not
    /// stale audio.
    /// </remarks>
    public int CopyTo(Span<float> destination)
    {
        lock (_gate)
        {
            var wanted = Math.Min(_count, destination.Length);
            CopyMostRecentUnlocked(destination, wanted);
            return wanted;
        }
    }

    /// <summary>Everything currently retained, oldest first. Allocates -- for tests and for the
    /// once-per-dictation arm, never for the device callback.</summary>
    public float[] Snapshot()
    {
        lock (_gate)
        {
            var samples = new float[_count];
            CopyMostRecentUnlocked(samples, _count);
            return samples;
        }
    }

    /// <summary>Drops everything retained. Called when a device is lost or swapped, so audio
    /// from the old device can never lead a capture from the new one.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _writeIndex = 0;
            _count = 0;
        }
    }

    private void CopyMostRecentUnlocked(Span<float> destination, int wanted)
    {
        if (wanted == 0)
        {
            return;
        }

        // The newest sample sits just behind the write cursor, so the run of `wanted` samples
        // ending there starts `wanted` slots back -- modulo arithmetic, twice, because C#'s %
        // keeps the sign of the dividend.
        var start = ((_writeIndex - wanted) % _ring.Length + _ring.Length) % _ring.Length;
        var untilWrap = _ring.Length - start;
        if (wanted <= untilWrap)
        {
            _ring.AsSpan(start, wanted).CopyTo(destination);
        }
        else
        {
            _ring.AsSpan(start, untilWrap).CopyTo(destination);
            _ring.AsSpan(0, wanted - untilWrap).CopyTo(destination[untilWrap..]);
        }
    }
}
