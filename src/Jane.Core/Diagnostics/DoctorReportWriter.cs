using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jane.Core.Diagnostics;

/// <summary>Serialises a <see cref="DoctorReport"/> to disk.</summary>
public static class DoctorReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(DoctorReport report, string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);

        // Write-then-move so a report is never read half-written, and a crashed run leaves the
        // previous report intact rather than a truncated one.
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}
