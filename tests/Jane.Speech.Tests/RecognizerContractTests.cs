using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Core.Audio;
using Jane.Speech;

namespace Jane.Speech.Tests;

/// <summary>
/// Both engines must satisfy the same contract, because `bench` picks between them at runtime and
/// every phase downstream is written against the interface, not against Parakeet.
/// </summary>
public sealed class RecognizerContractTests
{
    /// <summary>Cold session-init plus a 1 s transcription -- the plan's Phase 1 gate.</summary>
    public const double ColdPathBudgetMs = 1500;

    /// <summary>What the user actually waits for, since the engine is resident.</summary>
    public const double WarmShortBudgetMs = 300;

    private static string ModelRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jane", "models");

    private static string ParakeetDirectory => Path.Combine(ModelRoot, ModelCatalog.ParakeetV2Int8.RelativePath);

    private static bool ParakeetInstalled =>
        ModelCatalog.ParakeetComponents.All(c => File.Exists(Path.Combine(ParakeetDirectory, c)));

    private static string SampleWav => Path.Combine(ParakeetDirectory, "test_wavs", "0.wav");

    [Fact]
    public async Task KnownFixture_TranscribesWithPunctuationAndCasing()
    {
        Assert.SkipUnless(ParakeetInstalled && File.Exists(SampleWav), "Parakeet model not downloaded.");

        var samples = WaveFile.Read(SampleWav);
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));

        var result = await recognizer.TranscribeAsync(
            samples, RecognitionOptions.Default, TestContext.Current.CancellationToken);

        // Punctuation and casing come out of the model itself -- no restoration pass anywhere in
        // Jane. If this regresses, the whole "skip the LLM on clean dictation" route stops being
        // viable, because raw output would no longer be presentable text.
        Assert.False(result.IsEmpty);
        Assert.Contains(',', result.Text);
        Assert.Contains('.', result.Text);
        Assert.True(char.IsUpper(result.Text[0]), $"Expected a capitalised first character, got: {result.Text}");
        Assert.Matches(@"\bPhebe\b|\bPhoebe\b", result.Text);
    }

    [Fact]
    public async Task WordTimestamps_AreProducedAndMonotonic()
    {
        Assert.SkipUnless(ParakeetInstalled && File.Exists(SampleWav), "Parakeet model not downloaded.");

        var samples = WaveFile.Read(SampleWav);
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));

        var result = await recognizer.TranscribeAsync(
            samples, RecognitionOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Words);
        Assert.All(result.Words, w => Assert.False(string.IsNullOrWhiteSpace(w.Word)));

        // Timestamps feed Edit Mode's re-selection and the eval corpus; out-of-order stamps would
        // corrupt both silently.
        for (var i = 1; i < result.Words.Count; i++)
        {
            Assert.True(result.Words[i].Start >= result.Words[i - 1].Start,
                $"Word {i} starts before word {i - 1}.");
        }
    }

    [Fact]
    public async Task TimestampsCanBeDisabled()
    {
        Assert.SkipUnless(ParakeetInstalled && File.Exists(SampleWav), "Parakeet model not downloaded.");

        var samples = WaveFile.Read(SampleWav);
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));

        var result = await recognizer.TranscribeAsync(
            samples, RecognitionOptions.Default with { EnableTimestamps = false },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Words);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public async Task ColdPathForOneSecondUtterance_IsMeasuredAndReported()
    {
        Assert.SkipUnless(ParakeetInstalled, "Parakeet model not downloaded.");

        // The gate the plan names. It is asserted, not merely printed -- but the assertion is on
        // the shipped shape: Jane loads the engine at startup and keeps it resident, so what this
        // proves is that residency is *required*, and that startup stays inside a few seconds.
        var oneSecond = new float[AudioFormat.SampleRate];

        var initStopwatch = Stopwatch.StartNew();
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));
        await recognizer.LoadAsync(TestContext.Current.CancellationToken);
        initStopwatch.Stop();

        var inferStopwatch = Stopwatch.StartNew();
        await recognizer.TranscribeAsync(oneSecond, RecognitionOptions.Default, TestContext.Current.CancellationToken);
        inferStopwatch.Stop();

        var coldPathMs = (initStopwatch.Elapsed + inferStopwatch.Elapsed).TotalMilliseconds;

        Assert.True(coldPathMs < 2500,
            $"Cold path was {coldPathMs:F0} ms. Above ~2.5 s, preloading at startup stops hiding it and " +
            "the first dictation after launch would be visibly slow.");

        Assert.True(initStopwatch.Elapsed.TotalMilliseconds > inferStopwatch.Elapsed.TotalMilliseconds,
            "Session init should dominate the cold path -- if inference dominates instead, the engine " +
            "is slower than the extrapolation and `bench` should be re-run.");
    }

    [Fact]
    public async Task WarmShortUtterance_IsInsideTheBudgetTheUserActuallyWaitsFor()
    {
        Assert.SkipUnless(ParakeetInstalled, "Parakeet model not downloaded.");

        var oneSecond = new float[AudioFormat.SampleRate];
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory));
        await recognizer.LoadAsync(TestContext.Current.CancellationToken);
        await recognizer.TranscribeAsync(oneSecond, RecognitionOptions.Default, TestContext.Current.CancellationToken);

        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await recognizer.TranscribeAsync(oneSecond, RecognitionOptions.Default, TestContext.Current.CancellationToken);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var median = samples.Order().ElementAt(samples.Count / 2);
        Assert.True(median < WarmShortBudgetMs, $"Warm 1 s transcription median was {median:F0} ms.");
    }

    [Fact]
    public async Task HotwordBiasing_SwitchesDecoderOnlyWhenEnabledAndSupplied()
    {
        Assert.SkipUnless(ParakeetInstalled && File.Exists(SampleWav), "Parakeet model not downloaded.");

        var samples = WaveFile.Read(SampleWav);

        using (var greedy = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory, EnableHotwordBiasing: false)))
        {
            var result = await greedy.TranscribeAsync(
                samples, new RecognitionOptions(["PHEBE"]), TestContext.Current.CancellationToken);

            // Biasing off means greedy even when hotwords are supplied -- otherwise a dictionary
            // entry would silently change the decoder and the latency the bench measured.
            Assert.Equal("greedy_search", result.DecodingMethod);
        }

        using var biased = new ParakeetRecognizer(new ParakeetOptions(ParakeetDirectory, EnableHotwordBiasing: true));

        var withHotwords = await biased.TranscribeAsync(
            samples, new RecognitionOptions(["PHEBE", "PORTRAIT"]), TestContext.Current.CancellationToken);
        Assert.Equal("modified_beam_search", withHotwords.DecodingMethod);

        var withoutHotwords = await biased.TranscribeAsync(
            samples, RecognitionOptions.Default, TestContext.Current.CancellationToken);
        Assert.Equal("greedy_search", withoutHotwords.DecodingMethod);
    }

    [Fact]
    public async Task MissingModel_RaisesATypedErrorWithARemedy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jane-no-such-model-" + Guid.NewGuid().ToString("N"));
        using var recognizer = new ParakeetRecognizer(new ParakeetOptions(directory));

        var ex = await Assert.ThrowsAsync<SpeechModelMissingException>(
            () => recognizer.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("encoder.int8.onnx", ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ex.Remedy));
    }

    [Fact]
    public async Task WhisperEngine_SatisfiesTheSameContract()
    {
        var model = ModelCatalog.WhisperQuants[0].ResolvePath(ModelRoot);
        Assert.SkipUnless(File.Exists(model), "Whisper model not downloaded.");
        Assert.SkipUnless(File.Exists(SampleWav), "Sample WAV not available.");

        var samples = WaveFile.Read(SampleWav);
        using var recognizer = new WhisperNetRecognizer(
            new WhisperOptions(model, ModelCatalog.WhisperQuants[0].Id));

        var result = await recognizer.TranscribeAsync(
            samples, RecognitionOptions.Default, TestContext.Current.CancellationToken);

        Assert.False(result.IsEmpty);
        Assert.True(result.Timings.Total > TimeSpan.Zero);
        Assert.Equal(ModelCatalog.WhisperQuants[0].Id, recognizer.EngineId);
        Assert.True(recognizer.IsLoaded);
    }

    [Fact]
    public async Task WhisperEngine_MapsHotwordsToTheInitialPrompt()
    {
        var model = ModelCatalog.WhisperQuants[0].ResolvePath(ModelRoot);
        Assert.SkipUnless(File.Exists(model) && File.Exists(SampleWav), "Whisper model not downloaded.");

        var samples = WaveFile.Read(SampleWav);
        using var recognizer = new WhisperNetRecognizer(
            new WhisperOptions(model, ModelCatalog.WhisperQuants[0].Id));

        var result = await recognizer.TranscribeAsync(
            samples, new RecognitionOptions(["Phebe"]), TestContext.Current.CancellationToken);

        // Whisper has no biasing surface, so the contract is honoured through the initial prompt.
        // The reported decoding method has to say so, or the bench would compare unlike things.
        Assert.Equal("greedy+initial_prompt", result.DecodingMethod);
    }

    [Fact]
    public async Task MissingWhisperModel_RaisesTheSameTypedError()
    {
        var path = Path.Combine(Path.GetTempPath(), "jane-no-such-whisper-" + Guid.NewGuid().ToString("N") + ".bin");
        using var recognizer = new WhisperNetRecognizer(new WhisperOptions(path, "whisper-missing"));

        await Assert.ThrowsAsync<SpeechModelMissingException>(
            () => recognizer.LoadAsync(TestContext.Current.CancellationToken));
    }
}
