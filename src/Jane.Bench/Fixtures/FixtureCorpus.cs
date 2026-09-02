using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jane.Bench.Fixtures;

/// <summary>What a fixture is meant to stress. Bench and eval report per category.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixtureCategory>))]
public enum FixtureCategory
{
    /// <summary>Ordinary dictation. The bulk of real use.</summary>
    Prose,

    /// <summary>Domain vocabulary a general model has weak priors for.</summary>
    Jargon,

    /// <summary>Names. The single most common thing users report getting wrong.</summary>
    ProperNouns,

    /// <summary>Identifiers spoken aloud -- camelCase, snake_case, dotted paths.</summary>
    CodeIdentifiers,

    /// <summary>"no wait, make that Tuesday". The LLM pass has to resolve these, not transcribe them.</summary>
    SelfCorrection,

    /// <summary>Spoken structure the formatter must infer.</summary>
    Lists,

    /// <summary>Additive noise, to check the VAD and the recogniser degrade sensibly.</summary>
    Noisy,

    /// <summary>Very short -- the cold-path gate's shape.</summary>
    Short,
}

/// <summary>Where a fixture's audio came from. Affects how much weight its WER deserves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixtureSource>))]
public enum FixtureSource
{
    /// <summary>
    /// Windows SAPI text-to-speech. Fully local and free, deterministic, and it lets the corpus
    /// cover every content category without a recording session. Its weakness is real: synthetic
    /// speech is cleaner than a person at a desk, so absolute WER from these clips is optimistic.
    /// They are used for *relative* comparisons -- engine against engine, context on against
    /// off -- which is what the bench and the regression gate actually need.
    /// </summary>
    SynthesizedTts,

    /// <summary>A human recording with a checked licence.</summary>
    PublicDomain,

    /// <summary>
    /// Recorded by the user with Jane's own capture path. The most representative source there
    /// is; drop files in and add rows and everything downstream picks them up.
    /// </summary>
    OwnVoice,
}

/// <param name="Reference">Ground truth, including punctuation and casing -- both are scored.</param>
/// <param name="Hotwords">Terms a dictionary or Deep Context would supply. Drives the biasing tests.</param>
/// <param name="ExpectedFormatted">
/// What the LLM pass should turn <see cref="Reference"/> into. Null where formatting is a no-op.
/// </param>
public sealed record FixtureEntry(
    string Id,
    string File,
    FixtureCategory Category,
    FixtureSource Source,
    string Reference,
    IReadOnlyList<string>? Hotwords = null,
    string? ExpectedFormatted = null,
    string? Attribution = null,
    string? License = null);

/// <summary>
/// The fixture corpus, as a JSONL manifest beside the audio.
/// </summary>
/// <remarks>
/// JSONL rather than a single JSON array so a new fixture is a one-line append and a diff shows
/// exactly what was added -- this file is meant to grow as the user finds things Jane gets wrong.
/// </remarks>
public static class FixtureCorpus
{
    public const string ManifestFileName = "manifest.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DirectoryFor(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "fixtures", "audio");

    public static string ManifestPath(string repoRoot) =>
        Path.Combine(DirectoryFor(repoRoot), ManifestFileName);

    public static IReadOnlyList<FixtureEntry> Load(string repoRoot)
    {
        var path = ManifestPath(repoRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<FixtureEntry>(line, Json)!)
            .ToArray();
    }

    public static async Task SaveAsync(string repoRoot, IEnumerable<FixtureEntry> entries, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(repoRoot);
        Directory.CreateDirectory(directory);

        var lines = entries.Select(e => JsonSerializer.Serialize(e, Json));
        await File.WriteAllLinesAsync(ManifestPath(repoRoot), lines, cancellationToken);
    }

    public static string ResolveAudio(string repoRoot, FixtureEntry entry) =>
        Path.Combine(DirectoryFor(repoRoot), entry.File);

    /// <summary>
    /// The scripts the corpus is built from.
    /// </summary>
    /// <remarks>
    /// Written to be spoken, not read: contractions, filler, and the kind of self-correction a
    /// person actually makes mid-sentence. A corpus of clean written sentences would make every
    /// engine look better than it is and would never exercise the formatter at all.
    /// </remarks>
    public static IReadOnlyList<FixtureScript> Scripts { get; } =
    [
        new("prose-01", FixtureCategory.Prose,
            "I'll take a look at the pull request this afternoon and leave some comments on the parts I'm unsure about.",
            ExpectedFormatted: "I'll take a look at the pull request this afternoon and leave some comments on the parts I'm unsure about."),

        new("prose-02", FixtureCategory.Prose,
            "The meeting moved to Thursday because half the team is out on Wednesday, so let's push the demo back a day as well."),

        new("short-01", FixtureCategory.Short, "Send it."),

        new("short-02", FixtureCategory.Short, "Looks good to me."),

        new("jargon-01", FixtureCategory.Jargon,
            "We're running Kubernetes on bare metal with Cilium for networking and Prometheus scraping every fifteen seconds.",
            Hotwords: ["Kubernetes", "Cilium", "Prometheus"]),

        new("jargon-02", FixtureCategory.Jargon,
            "The quantized ONNX encoder runs on the CPU execution provider, so inference never touches VRAM.",
            Hotwords: ["ONNX", "VRAM", "CPU"]),

        new("proper-01", FixtureCategory.ProperNouns,
            "Ask Siobhan and Rajesh whether the Helsinki office signed off on the Anthropic contract.",
            Hotwords: ["Siobhan", "Rajesh", "Helsinki", "Anthropic"]),

        new("code-01", FixtureCategory.CodeIdentifiers,
            "Call get user by id in the auth service, then check the return value against null before you dereference it.",
            Hotwords: ["getUserById", "AuthService"]),

        new("code-02", FixtureCategory.CodeIdentifiers,
            "The config lives in source, jane dot core, settings, settings store dot C S.",
            Hotwords: ["SettingsStore", "Jane.Core"]),

        new("selfcorrect-01", FixtureCategory.SelfCorrection,
            "Um, so, let's ship it on Tuesday. No wait, make that Wednesday, because Tuesday is the release freeze.",
            ExpectedFormatted: "Let's ship it on Wednesday, because Tuesday is the release freeze."),

        new("selfcorrect-02", FixtureCategory.SelfCorrection,
            "I think the uh the timeout should be thirty seconds. Scratch that, sixty seconds, it times out under load.",
            ExpectedFormatted: "I think the timeout should be sixty seconds; it times out under load."),

        new("list-01", FixtureCategory.Lists,
            "Three things left. First, wire up the governor. Second, finish the injection tests. Third, write the notice file.",
            ExpectedFormatted: "Three things left:\n\n1. Wire up the governor.\n2. Finish the injection tests.\n3. Write the notice file."),

        new("noisy-01", FixtureCategory.Noisy,
            "Can you double check the deployment before it goes out tonight?", AddNoise: true),
    ];
}

/// <param name="AddNoise">Mix in broadband noise at a fixed SNR after synthesis.</param>
public sealed record FixtureScript(
    string Id,
    FixtureCategory Category,
    string Text,
    IReadOnlyList<string>? Hotwords = null,
    string? ExpectedFormatted = null,
    bool AddNoise = false);
