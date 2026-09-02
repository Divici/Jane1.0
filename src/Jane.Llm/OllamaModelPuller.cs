using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jane.Llm;

/// <param name="Completed">Bytes so far. Zero until the server starts sending layers.</param>
public sealed record PullProgress(string Status, long Completed, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Completed / (double)Total, 0, 1);
}

/// <summary>
/// Pulls an Ollama model through the supervised server, reporting progress as it goes.
/// </summary>
/// <remarks>
/// This exists so onboarding does not leave a manual <c>ollama pull</c> as homework, which is a
/// stated Phase 11 requirement. Going through Jane's own supervised server rather than shelling
/// out to the CLI means the download lands in the same model store the app will read from, and a
/// half-finished pull resumes on the next attempt because Ollama's own layer store does.
/// </remarks>
public sealed class OllamaModelPuller(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether the server already has the model locally.</summary>
    public async Task<bool> IsPresentAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var tags = await http.GetFromJsonAsync<TagsResponse>("/api/tags", Json, cancellationToken);
            return tags?.Models?.Any(m =>
                m.Name.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async Task PullAsync(
        string model, IProgress<PullProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }, options: Json),
        };

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Newline-delimited JSON, one object per progress update. Read line by line rather than
        // buffering: a 2.5 GB pull takes minutes and a progress bar that only moves at the end is
        // worse than no progress bar at all.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullResponse? update;
            try
            {
                update = JsonSerializer.Deserialize<PullResponse>(line, Json);
            }
            catch (JsonException)
            {
                // A malformed line is not worth failing a multi-gigabyte download over.
                continue;
            }

            if (update is null)
            {
                continue;
            }

            if (update.Error is { Length: > 0 } error)
            {
                throw new OllamaException($"Pulling {model} failed: {error}");
            }

            progress?.Report(new PullProgress(update.Status ?? "pulling", update.Completed, update.Total));
        }
    }

    private sealed record PullResponse(
        string? Status,
        string? Error,
        long Completed,
        long Total);

    private sealed record TagsResponse(IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(string Name);
}
