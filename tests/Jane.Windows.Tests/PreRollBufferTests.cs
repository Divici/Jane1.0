using Jane.Core.Abstractions;
using Jane.Windows.Audio;

namespace Jane.Windows.Tests;

/// <summary>
/// The ring that makes "hold the key and start talking" work. Everything here is arithmetic --
/// no device, no thread, no timing -- so a regression shows up as a failing assert rather than
/// as a clipped first word months later.
/// </summary>
public sealed class PreRollBufferTests
{
    // A ramp -- sample i carries the value i -- so any reordering, off-by-one or lost wrap is
    // visible as a wrong number rather than as "the audio sounds odd".
    private static float[] Ramp(int start, int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = start + i;
        }

        return samples;
    }

    [Fact]
    public void RetainsAudioFromThreeHundredMillisecondsBeforeKeyDown()
    {
        // The named plan test. A key press is physically registered tens of milliseconds after
        // the user has started speaking, so the buffer must be able to hand back audio from
        // before the press -- 300 ms is comfortably inside the 500 ms window.
        var buffer = PreRollBuffer.ForWindow(TimeSpan.FromMilliseconds(500));
        buffer.Write(Ramp(0, AudioFormat.SampleRate)); // one second of audio, then "key-down"

        var preRoll = buffer.Snapshot();

        var samplesIn300Ms = AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(300));
        Assert.Equal(AudioFormat.SampleRate - samplesIn300Ms, preRoll[^samplesIn300Ms]);
        Assert.Equal(AudioFormat.SampleRate - 1, preRoll[^1]);
    }

    [Fact]
    public void DefaultWindowIsOneSecond()
    {
        // Was 500 ms, sized for the gap between "started speaking" and "key registered". Widened
        // after the field report of clipped first words: a second also covers somebody who begins
        // speaking as they press, and it costs 64 KB.
        Assert.Equal(
            AudioFormat.SamplesFor(TimeSpan.FromSeconds(1)),
            PreRollBuffer.ForWindow(PreRollBuffer.DefaultWindow).Capacity);
    }

    [Fact]
    public void RetainsOnlyTheMostRecentWindowOnceFull()
    {
        var buffer = new PreRollBuffer(capacitySamples: 8);

        buffer.Write(Ramp(0, 20));

        Assert.Equal(8, buffer.Count);
        Assert.Equal<float[]>([12, 13, 14, 15, 16, 17, 18, 19], buffer.Snapshot());
    }

    [Fact]
    public void WrapsAcrossTheRingWithoutReorderingSamples()
    {
        // Three writes that each straddle the wrap point. A naive implementation that copies
        // the backing array verbatim passes the "full buffer" case above and fails this one.
        var buffer = new PreRollBuffer(capacitySamples: 8);

        buffer.Write(Ramp(0, 5));
        buffer.Write(Ramp(5, 5));
        buffer.Write(Ramp(10, 3));

        Assert.Equal<float[]>([5, 6, 7, 8, 9, 10, 11, 12], buffer.Snapshot());
    }

    [Fact]
    public void PartiallyFilledBufferReturnsOnlyWhatItHas()
    {
        var buffer = new PreRollBuffer(capacitySamples: 8);

        buffer.Write(Ramp(0, 3));

        Assert.Equal(3, buffer.Count);
        Assert.Equal<float[]>([0, 1, 2], buffer.Snapshot());
    }

    [Fact]
    public void CopyToATooSmallDestinationKeepsTheNewestSamples()
    {
        // Arming with a shorter pre-roll than the ring holds must give the audio nearest the
        // key press, not the oldest audio in the ring.
        var buffer = new PreRollBuffer(capacitySamples: 8);
        buffer.Write(Ramp(0, 8));

        var destination = new float[3];
        var written = buffer.CopyTo(destination);

        Assert.Equal(3, written);
        Assert.Equal<float[]>([5, 6, 7], destination);
    }

    [Fact]
    public void ClearDropsEverythingSoAReopenedDeviceCannotLeakOldAudio()
    {
        var buffer = new PreRollBuffer(capacitySamples: 8);
        buffer.Write(Ramp(0, 8));

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void EmptyBufferSnapshotIsEmptyRatherThanNull()
    {
        Assert.Empty(new PreRollBuffer(capacitySamples: 8).Snapshot());
    }

    [Fact]
    public void AWriteLongerThanTheRingKeepsOnlyItsTail()
    {
        var buffer = new PreRollBuffer(capacitySamples: 4);

        buffer.Write(Ramp(0, 100));

        Assert.Equal<float[]>([96, 97, 98, 99], buffer.Snapshot());
    }

    [Fact]
    public void CapacityMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreRollBuffer(capacitySamples: 0));
    }
}
