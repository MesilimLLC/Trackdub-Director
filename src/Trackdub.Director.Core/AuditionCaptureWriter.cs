using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Persists every synthesized audition take (winners and losers) plus the
/// manifest when a capture output directory is configured. Manifest entries
/// only name files that were actually written.
/// </summary>
public static class AuditionCaptureWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Writes all synthesized takes that carry audio bytes under
    /// <c>{outputDir}/media/audition/</c> and refreshes
    /// <c>audition-manifest.json</c>. Returns the number of WAV files written.
    /// </summary>
    public static async Task<int> WriteAsync(string outputDir, AuditionReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentNullException.ThrowIfNull(report);

        string auditionDir = Path.Combine(outputDir, "media", "audition");
        Directory.CreateDirectory(auditionDir);

        var toWrite = new List<(string FileName, byte[] Bytes)>();
        foreach (AuditionSegmentReport segment in report.Segments)
        {
            foreach (AuditionTakeReport take in segment.Takes)
            {
                if (!take.Synthesized || take.AudioBytes is null || string.IsNullOrWhiteSpace(take.AudioFileName))
                {
                    continue;
                }

                toWrite.Add((take.AudioFileName, take.AudioBytes));
            }
        }

        if (toWrite.Select(t => t.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != toWrite.Count)
        {
            throw new ArgumentException("Two synthesized takes map to the same file name.", nameof(report));
        }

        foreach ((string fileName, byte[] bytes) in toWrite)
        {
            await WriteTakeAsync(auditionDir, fileName, bytes, cancellationToken).ConfigureAwait(false);
        }

        string manifest = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "audition-manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
        return toWrite.Count;
    }

    public static async Task WriteTakeAsync(string auditionDir, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditionDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(bytes);

        string root = Path.GetFullPath(auditionDir) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(auditionDir, fileName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Take file name resolves outside the audition directory.", nameof(fileName));
        }

        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
    }
}
