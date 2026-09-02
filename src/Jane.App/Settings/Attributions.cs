namespace Jane.App.Settings;

/// <param name="Component">
/// The name as it appears in <c>NOTICE.md</c>. The two are asserted against each other, so this
/// is a key rather than a label -- a prettier name here would break the check that the shipped
/// attribution file and the visible About view agree.
/// </param>
/// <param name="Detail">The build actually used, and anything a reader would otherwise ask.</param>
public sealed record AttributionEntry(
    string Component,
    string Role,
    string License,
    string Holder,
    string Source,
    string? Detail = null);

/// <summary>
/// Everything Jane is built from, and under what licence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a licence obligation, not documentation.</b> Parakeet-TDT-0.6B-v2 is CC-BY-4.0 and
/// Silero VAD is MIT, and both require attribution in the shipped product -- not in a file in a
/// repository the user never opens. <c>NOTICE.md</c> is the canonical text and ships beside the
/// binary; this list is the same information rendered where a person will actually see it, and a
/// test asserts every entry here appears there with the same licence.
/// </para>
/// <para>
/// Adding a dependency means adding a row here and a row in <c>NOTICE.md</c>. The test fails
/// until both exist, which is the only mechanism that keeps an attribution surface honest over
/// time.
/// </para>
/// </remarks>
public static class Attributions
{
    /// <summary>The weights. These are the entries the licences actually oblige.</summary>
    public static IReadOnlyList<AttributionEntry> Models { get; } =
    [
        new("Parakeet-TDT-0.6B-v2",
            "Turns speech into text, with punctuation and casing already in it.",
            "CC-BY-4.0",
            "NVIDIA Corporation",
            "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2",
            "int8 ONNX export packaged for sherpa-onnx by k2-fsa. Used unmodified."),

        new("Silero VAD",
            "Detects whether you actually spoke, and trims the silence either side.",
            "MIT",
            "Silero Team",
            "https://github.com/snakers4/silero-vad"),

        new("Qwen3",
            "Cleans the transcript: removes filler, resolves spoken self-corrections, applies your instructions.",
            "Apache-2.0",
            "Alibaba Cloud / Qwen Team",
            "https://huggingface.co/Qwen",
            "qwen3:4b-instruct on the GPU route and qwen3:1.7b on the CPU route, served through Ollama. Jane changes sampling parameters only; the weights are unmodified."),

        new("Whisper",
            "The second speech engine, benchmarked against Parakeet on every machine.",
            "MIT",
            "OpenAI",
            "https://huggingface.co/openai/whisper-large-v3-turbo",
            "large-v3-turbo, GGML quantisations published by Georgi Gerganov."),
    ];

    /// <summary>The code Jane runs on.</summary>
    public static IReadOnlyList<AttributionEntry> Runtimes { get; } =
    [
        new("sherpa-onnx", "Runs the Parakeet model, in process, on the CPU.", "Apache-2.0", "k2-fsa", "https://github.com/k2-fsa/sherpa-onnx"),
        new("ONNX Runtime", "The inference engine underneath sherpa-onnx.", "MIT", "Microsoft", "https://github.com/microsoft/onnxruntime"),
        new("Whisper.net", "The .NET binding for the second engine.", "MIT", "Sandro Hanea", "https://github.com/sandrohanea/whisper.net"),
        new("whisper.cpp", "The second engine's inference code.", "MIT", "Georgi Gerganov", "https://github.com/ggml-org/whisper.cpp"),
        new("Ollama", "Serves the language model on this machine, supervised by Jane.", "MIT", "Ollama", "https://github.com/ollama/ollama"),
        new("llama.cpp", "The inference engine underneath Ollama.", "MIT", "Georgi Gerganov", "https://github.com/ggml-org/llama.cpp"),
        new("NAudio", "Opens the microphone through WASAPI.", "MIT", "Mark Heath", "https://github.com/naudio/NAudio"),
        new("H.NotifyIcon", "The tray icon.", "MIT", "Havendv", "https://github.com/HavenDV/H.NotifyIcon"),
        new("SharpZipLib", "Unpacks the bzip2 model archives, which .NET cannot read on its own.", "MIT", "IC#Code", "https://github.com/icsharpcode/SharpZipLib"),
        new("Microsoft.Data.Sqlite", "Reads and writes jane.db.", "MIT", "Microsoft", "https://github.com/dotnet/efcore"),
        new("SQLite", "The database itself.", "Public domain", "D. Richard Hipp", "https://sqlite.org/copyright.html"),
        new(".NET / WPF", "The runtime and the windows.", "MIT", "Microsoft", "https://github.com/dotnet/wpf"),
        new("xUnit.net", "Jane's tests. Not shipped in the binary.", "Apache-2.0", "xUnit.net contributors", "https://github.com/xunit/xunit"),
    ];

    public static IReadOnlyList<AttributionEntry> All { get; } = [.. Models, .. Runtimes];

    /// <summary>
    /// What Jane sends, and where. Shown in About beside the licences because it is the other
    /// half of the same promise.
    /// </summary>
    public static string NetworkStatement { get; } =
        "Nothing, on the dictation path. No telemetry, no crash reporting, no update check. Every model runs on this machine, and a test asserts that every socket the dictation pipeline opens is loopback. The one exception is deliberate and user-initiated: downloading the model weights above, from github.com and huggingface.co, the first time you ask for them. Each download is verified against a SHA-256 pinned in the source before it is unpacked.";

    /// <summary>What Jane keeps. The same words the history window opens with.</summary>
    public static string RetentionStatement { get; } =
        "Transcript history is stored in plaintext SQLite and is retained until you delete it, including any on-screen text Deep Context read. Audio is never written to disk. History can be searched, individually deleted, or erased entirely.";
}
