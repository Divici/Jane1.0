using System.Globalization;

namespace Jane.Speech;

/// <summary>What kind of artefact a catalogue entry is, which decides how it is unpacked.</summary>
public enum ModelArtifactKind
{
    /// <summary>A single file, e.g. <c>silero_vad.onnx</c> or a whisper.cpp GGML binary.</summary>
    SingleFile,

    /// <summary>A bzip2 tarball unpacked into a directory, as sherpa-onnx ships its models.</summary>
    TarBz2,
}

/// <param name="Id">Stable key used in settings and bench reports.</param>
/// <param name="Sha256">
/// Lower-case hex of the downloaded artefact. Verified before anything is unpacked -- a truncated
/// 482 MB download otherwise fails as an unreadable ONNX graph on the user's first dictation.
/// </param>
/// <param name="SizeBytes">Expected size, so a wrong-length response is caught before hashing it.</param>
/// <param name="RelativePath">
/// Where the artefact lands under the model root: a filename for <see cref="ModelArtifactKind.SingleFile"/>,
/// or the directory the tarball expands to.
/// </param>
/// <param name="License">Shipped in the About view and NOTICE.md. Parakeet's CC-BY-4.0 makes this a licence obligation, not a courtesy.</param>
public sealed record ModelAsset(
    string Id,
    Uri Url,
    string Sha256,
    long SizeBytes,
    ModelArtifactKind Kind,
    string RelativePath,
    string License,
    string Attribution)
{
    public string ResolvePath(string modelRoot) => Path.Combine(modelRoot, RelativePath);

    public string SizeDescription =>
        (SizeBytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// Every model weight Jane can download, pinned by URL and SHA-256.
/// </summary>
/// <remarks>
/// Nothing here is committed to the repository -- a 622 MB encoder in git history cannot be
/// removed later without rewriting it. <see cref="ModelDownloader"/> fetches and verifies these
/// into <c>%LOCALAPPDATA%\Jane\models</c> on first run.
///
/// Hashes were computed from the real downloads on 2026-09-02. They are pins, not guesses: an
/// upstream re-release with the same filename is exactly what a checksum is for.
/// </remarks>
public static class ModelCatalog
{
    /// <summary>
    /// The locked ASR model: English-specialised, int8, CPU, with native punctuation, casing and
    /// word timestamps. English-only is what unlocks v2 over the multilingual v3, which is both
    /// less accurate on English and slower.
    /// </summary>
    public static ModelAsset ParakeetV2Int8 { get; } = new(
        Id: "parakeet-tdt-0.6b-v2-int8",
        Url: new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2"),
        Sha256: "157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad",
        SizeBytes: 482_468_385,
        Kind: ModelArtifactKind.TarBz2,
        RelativePath: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
        License: "CC-BY-4.0",
        Attribution: "NVIDIA Parakeet-TDT-0.6B-v2, packaged for sherpa-onnx by k2-fsa. Licensed CC-BY-4.0.");

    /// <summary>
    /// Voice activity detection. A missing VAD model is a typed error, never a silent
    /// disable -- `OpenWhispr#1057` is the case where that silence shipped.
    /// </summary>
    public static ModelAsset SileroVad { get; } = new(
        Id: "silero-vad",
        Url: new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"),
        Sha256: "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6",
        SizeBytes: 643_854,
        Kind: ModelArtifactKind.SingleFile,
        RelativePath: "silero_vad.onnx",
        License: "MIT",
        Attribution: "Silero VAD by Silero Team. Licensed MIT.");

    /// <summary>
    /// Second engine, benched against Parakeet so the extrapolated CPU figure is validated rather
    /// than trusted.
    /// </summary>
    /// <remarks>
    /// The plan lists Q4_0 / Q5_0 / Q8_0 as the quantisation axis. Upstream
    /// (<c>ggerganov/whisper.cpp</c>) ships only q5_0 and q8_0 for large-v3-turbo -- there is no
    /// Q4_0 turbo build to download, so the axis is the two that exist. This matters because the
    /// research flagged Q5-family quants as markedly slower than Q4 on CPU: if whisper loses the
    /// bench, "no Q4_0 available" is part of why, and that is recorded rather than hidden.
    /// </remarks>
    public static IReadOnlyList<ModelAsset> WhisperQuants { get; } =
    [
        new ModelAsset(
            Id: "whisper-large-v3-turbo-q5_0",
            Url: new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin"),
            Sha256: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            SizeBytes: 574_041_195,
            Kind: ModelArtifactKind.SingleFile,
            RelativePath: "ggml-large-v3-turbo-q5_0.bin",
            License: "MIT",
            Attribution: "OpenAI Whisper large-v3-turbo, GGML build by ggerganov. Licensed MIT."),
        new ModelAsset(
            Id: "whisper-large-v3-turbo-q8_0",
            Url: new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q8_0.bin"),
            Sha256: "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1",
            SizeBytes: 874_188_075,
            Kind: ModelArtifactKind.SingleFile,
            RelativePath: "ggml-large-v3-turbo-q8_0.bin",
            License: "MIT",
            Attribution: "OpenAI Whisper large-v3-turbo, GGML build by ggerganov. Licensed MIT."),
    ];

    /// <summary>Everything needed for a working dictation path, in download order.</summary>
    public static IReadOnlyList<ModelAsset> Required { get; } = [ParakeetV2Int8, SileroVad];

    public static IReadOnlyList<ModelAsset> All { get; } = [.. Required, .. WhisperQuants];

    public static ModelAsset ById(string id) =>
        All.FirstOrDefault(a => a.Id == id)
        ?? throw new KeyNotFoundException($"No model asset with id '{id}'.");

    /// <summary>
    /// Files inside the Parakeet tarball that must all be present for the directory to count as
    /// a complete install. Checked after extraction and again at load.
    /// </summary>
    public static IReadOnlyList<string> ParakeetComponents { get; } =
        ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];
}
