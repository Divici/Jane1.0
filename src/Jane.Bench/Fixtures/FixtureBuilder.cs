using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Jane.Core.Abstractions;
using Jane.Core.Audio;

namespace Jane.Bench.Fixtures;

/// <summary>
/// Builds the fixture corpus by synthesising each script with Windows' own text-to-speech.
/// </summary>
/// <remarks>
/// The plan calls for own-voice recordings. Those need the user at a microphone, which a fully
/// automated `bench` cannot arrange -- and the plan is equally firm that `bench` must complete
/// with stdin closed. Synthesising with SAPI gives a corpus that is local, free, deterministic
/// and reproducible on any Windows machine, and covers every content category the plan lists.
///
/// The trade is real and worth stating: SAPI speech is cleaner than a person at a desk, so
/// absolute WER from these clips is optimistic. That does not hurt what the corpus is for. Every
/// gate in the plan is a *relative* comparison -- engine against engine, quantisation against
/// quantisation, biasing on against off, context on against off, this run against the committed
/// baseline -- and those hold as long as the audio is identical between arms, which it is.
///
/// Own-voice clips are a drop-in and are strictly better: record a WAV at 16 kHz mono, put it in
/// tests/fixtures/audio, and append a manifest row with Source = OwnVoice. Nothing downstream
/// treats them differently except that they are more representative.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FixtureBuilder(string repoRoot)
{
    public async Task<IReadOnlyList<FixtureEntry>> BuildAsync(bool force, CancellationToken cancellationToken)
    {
        var directory = FixtureCorpus.DirectoryFor(repoRoot);
        Directory.CreateDirectory(directory);

        var existing = FixtureCorpus.Load(repoRoot).ToDictionary(e => e.Id);
        var entries = new List<FixtureEntry>();

        using var synthesizer = new SpeechSynthesizer();
        var voice = SelectVoiceOnce(synthesizer);

        foreach (var script in FixtureCorpus.Scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = script.Id + ".wav";
            var path = Path.Combine(directory, fileName);

            if (force || !File.Exists(path))
            {
                Synthesize(synthesizer, voice, script, path);
            }

            entries.Add(new FixtureEntry(
                Id: script.Id,
                File: fileName,
                Category: script.Category,
                Source: FixtureSource.SynthesizedTts,
                Reference: script.Text,
                Hotwords: script.Hotwords,
                ExpectedFormatted: script.ExpectedFormatted ?? script.Text,
                Attribution: $"Synthesized with Windows SAPI ({voice ?? "default voice"}).",
                License: "Generated locally; no third-party rights."));
        }

        // Own-voice and public-domain rows already in the manifest are preserved. The generator
        // owns the synthesized rows and nothing else -- re-running it must never delete a
        // recording the user made.
        foreach (var kept in existing.Values.Where(e => e.Source != FixtureSource.SynthesizedTts))
        {
            entries.Add(kept);
        }

        await FixtureCorpus.SaveAsync(repoRoot, entries.OrderBy(e => e.Id, StringComparer.Ordinal), cancellationToken);
        return entries;
    }

    private static void Synthesize(SpeechSynthesizer synthesizer, string? voice, FixtureScript script, string path)
    {
        // SAPI will not write 16 kHz mono directly through SetOutputToWaveFile's default format,
        // so the format is stated explicitly -- Jane never resamples anywhere in the pipeline and
        // a 22 kHz fixture would be silently wrong rather than loudly rejected.
        var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
            AudioFormat.SampleRate,
            System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
            System.Speech.AudioFormat.AudioChannel.Mono);

        using (var stream = new MemoryStream())
        {
            synthesizer.SetOutputToAudioStream(stream, format);

            // A little leading and trailing silence: real dictation always has some, and a fixture
            // with none would let a broken VAD trim pass unnoticed.
            // Explicit en-US: PromptBuilder rejects the invariant culture outright, which is what
            // a machine with no culture data would otherwise hand it. Jane is English-only
            // anyway, so pinning it here is honest rather than a workaround.
            var builder = new PromptBuilder(CultureInfo.GetCultureInfo("en-US"));
            builder.AppendBreak(PromptBreak.Medium);
            builder.AppendText(script.Text);
            builder.AppendBreak(PromptBreak.Medium);
            synthesizer.Speak(builder);
            synthesizer.SetOutputToNull();

            stream.Position = 0;
            var samples = ReadRawPcm(stream);

            if (script.AddNoise)
            {
                AddNoise(samples, snrDb: 12);
            }

            WaveFile.Write(path, samples);
        }
    }

    /// <summary>
    /// SAPI's audio stream is headerless raw PCM at the requested format, so it is read directly
    /// rather than through <see cref="WaveFile"/>.
    /// </summary>
    private static float[] ReadRawPcm(Stream stream)
    {
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return samples;
    }

    /// <summary>Mixes in broadband noise at a fixed SNR, seeded so the fixture is reproducible.</summary>
    private static void AddNoise(float[] samples, double snrDb)
    {
        var signalPower = samples.Length == 0 ? 0 : samples.Sum(s => (double)s * s) / samples.Length;
        if (signalPower <= 0)
        {
            return;
        }

        var noisePower = signalPower / Math.Pow(10, snrDb / 10);
        var amplitude = Math.Sqrt(noisePower * 3);
        var random = new Random(20260902);

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Clamp(samples[i] + (((random.NextDouble() * 2) - 1) * amplitude), -1, 1);
        }
    }

    /// <summary>
    /// Picks a voice once, before any output is configured, and returns the one actually in use.
    /// </summary>
    /// <remarks>
    /// Selection is attempted but never required. <c>System.Speech</c>'s .NET (Core) port throws a
    /// <see cref="NullReferenceException"/> out of <c>SelectVoice</c> for the "Desktop" voices this
    /// machine has installed, even though <c>GetInstalledVoices</c> lists them as enabled. Since
    /// the corpus only needs to be *consistent between arms of the bench*, not identical across
    /// machines, falling back to the default voice costs nothing -- so the failure is caught and
    /// the voice actually used is recorded in the manifest instead.
    /// </remarks>
    private static string? SelectVoiceOnce(SpeechSynthesizer synthesizer)
    {
        var installed = synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .ToArray();

        foreach (var preferred in new[] { "Microsoft Zira Desktop", "Microsoft David Desktop", "Microsoft Zira", "Microsoft David" })
        {
            if (!installed.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                synthesizer.SelectVoice(preferred);
                return preferred;
            }
            catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException)
            {
                break;
            }
        }

        try
        {
            return synthesizer.Voice.Name;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            return installed.FirstOrDefault();
        }
    }
}
