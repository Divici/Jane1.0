using Jane.Core.Abstractions;

namespace Jane.Core.Audio;

/// <summary>
/// Minimal 16-bit PCM WAV reader and writer for the fixture corpus.
/// </summary>
/// <remarks>
/// Deliberately small and dependency-free, and deliberately in <c>Jane.Core</c>: the eval harness
/// and the bench both read fixtures, and neither should have to pull in an audio stack or the
/// Windows TFM to do it.
///
/// This is the only place Jane touches audio files at all. Dictation audio is never written to
/// disk -- only the fixture corpus is, and it is checked in as test data.
/// </remarks>
public static class WaveFile
{
    /// <summary>Reads a PCM WAV, downmixing to mono and rejecting anything not at 16 kHz.</summary>
    /// <exception cref="InvalidDataException">The file is not 16-bit PCM, or not 16 kHz.</exception>
    public static float[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static float[] Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        var channels = 1;
        var sampleRate = 0;
        var bitsPerSample = 16;

        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();

            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();

                if (format != 1)
                {
                    throw new InvalidDataException($"Only PCM is supported; format tag was {format}.");
                }

                if (size > 16)
                {
                    reader.ReadBytes(size - 16);
                }
            }
            else if (id == "data")
            {
                if (bitsPerSample != 16)
                {
                    throw new InvalidDataException($"Only 16-bit PCM is supported; file is {bitsPerSample}-bit.");
                }

                if (sampleRate != AudioFormat.SampleRate)
                {
                    throw new InvalidDataException(
                        $"Fixture must be {AudioFormat.SampleRate} Hz; file is {sampleRate} Hz. " +
                        "Jane never resamples -- the capture path produces 16 kHz and the models expect it.");
                }

                var frames = size / 2 / channels;
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        sum += reader.ReadInt16() / 32768f;
                    }

                    samples[i] = sum / channels;
                }

                return samples;
            }
            else
            {
                // Skip unknown chunks (LIST, fact, ...), honouring RIFF's word alignment.
                reader.ReadBytes(size + (size % 2));
            }
        }

        throw new InvalidDataException("WAV file has no data chunk.");
    }

    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate = AudioFormat.SampleRate)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        Write(stream, samples, sampleRate);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate = AudioFormat.SampleRate)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }
    }
}
