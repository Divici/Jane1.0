using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jane.Windows.Audio;

/// <summary>
/// Folds whatever format the endpoint hands back into 16 kHz mono float.
/// </summary>
/// <remarks>
/// Shared-mode WASAPI gives you the device *mix* format -- on this machine 48 kHz stereo float --
/// and nothing downstream of capture resamples: Parakeet and Silero VAD both take 16 kHz mono and
/// would happily accept a 48 kHz buffer while transcribing gibberish. Doing the fold here, at the
/// edge, is the only place it can be verified once.
/// <para>
/// The whole chain is NAudio's own managed DSP -- <c>WdlResamplingSampleProvider</c> -- so there
/// is no Media Foundation dependency and no COM apartment requirement on the callback thread.
/// </para>
/// </remarks>
public sealed class DeviceSampleConverter
{
    private readonly BufferedWaveProvider? _source;
    private readonly ISampleProvider? _chain;
    private float[] _samples = [];

    /// <summary>Assumes IEEE float, which is what shared-mode WASAPI mix formats use.</summary>
    public DeviceSampleConverter(int sampleRate, int channels)
        : this(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels))
    {
    }

    public DeviceSampleConverter(WaveFormat deviceFormat)
    {
        ArgumentNullException.ThrowIfNull(deviceFormat);

        IsPassthrough = deviceFormat is
        {
            SampleRate: AudioFormat.SampleRate,
            Channels: AudioFormat.Channels,
            Encoding: WaveFormatEncoding.IeeeFloat,
        };
        if (IsPassthrough)
        {
            return;
        }

        _source = new BufferedWaveProvider(deviceFormat, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,

            // Without this, an empty buffer reads as silence forever and the drain loop below
            // never terminates.
            ReadFully = false,
        };

        ISampleProvider chain = _source.ToSampleProvider();
        chain = deviceFormat.Channels switch
        {
            1 => chain,
            2 => new StereoToMonoSampleProvider(chain),

            // Above stereo there is no meaningful downmix for speech, so take the first channel.
            _ => new MultiplexingSampleProvider([chain], 1),
        };

        _chain = chain.WaveFormat.SampleRate == AudioFormat.SampleRate
            ? chain
            : new WdlResamplingSampleProvider(chain, AudioFormat.SampleRate);
    }

    /// <summary>True when the endpoint already speaks Jane's format and no work is needed.</summary>
    public bool IsPassthrough { get; }

    /// <summary>
    /// Converts one device callback's worth of bytes.
    /// </summary>
    /// <returns>
    /// 16 kHz mono float samples, in a buffer this instance reuses -- valid until the next call.
    /// Callers copy; that is what keeps the capture callback allocation-free once warm.
    /// </returns>
    public ReadOnlyMemory<float> Convert(ReadOnlySpan<byte> deviceBytes)
    {
        var frames = deviceBytes.Length / sizeof(float);
        if (IsPassthrough)
        {
            Grow(ref _samples, frames);
            MemoryMarshal.Cast<byte, float>(deviceBytes).CopyTo(_samples);
            return _samples.AsMemory(0, frames);
        }

        _source!.AddSamples(deviceBytes);

        var written = 0;
        while (true)
        {
            // Ask for exactly what the buffered input can produce, recomputed each pass. WDL's
            // resampler commits the input it prepared even when it cannot fill the request, so
            // asking for a round 4096 frames when only 160 are available silently discards the
            // callback -- which reads downstream as a microphone that captures ten milliseconds
            // and then goes quiet.
            var want = AvailableOutputFrames();
            if (want <= 0)
            {
                break;
            }

            Grow(ref _samples, written + want);
            var read = _chain!.Read(_samples.AsSpan(written, want));
            written += read;
            if (read < want)
            {
                break;
            }
        }

        return _samples.AsMemory(0, written);
    }

    private int AvailableOutputFrames()
    {
        var format = _source!.WaveFormat;
        var frames = _source.BufferedBytes / format.BlockAlign;
        return (int)((long)frames * AudioFormat.SampleRate / format.SampleRate);
    }

    private static void Grow<T>(ref T[] buffer, int required)
    {
        if (buffer.Length < required)
        {
            Array.Resize(ref buffer, Math.Max(required, buffer.Length * 2));
        }
    }
}
