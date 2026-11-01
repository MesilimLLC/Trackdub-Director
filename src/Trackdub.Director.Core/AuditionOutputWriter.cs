using System.Security.Cryptography;
using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Persists an audition run: every synthesized take as a WAV (winners and losers, under the
/// file names the report carries) and the manifest. The manifest only names files this writer
/// wrote, and records each file's SHA-256, so a reader can tell a take that was kept from one
/// that was only reported.
/// </summary>
public static class AuditionOutputWriter
{
    public const string ManifestFileName = "audition-manifest.json";

    public static readonly string[] MediaSubfolders = ["media", "audition"];

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Writes the takes and the manifest under <paramref name="outputDir"/>; returns the manifest path.</summary>
    public static async Task<string> WriteAsync(AuditionReport report, string outputDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);

        string auditionDir = Path.Combine([outputDir, .. MediaSubfolders]);
        Directory.CreateDirectory(auditionDir);

        List<(string FileName, byte[] Bytes)> takes = report.Segments
            .SelectMany(segment => segment.Takes)
            .Where(take => take.AudioBytes is not null && !string.IsNullOrEmpty(take.AudioFileName))
            .Select(take => (take.AudioFileName!, take.AudioBytes!))
            .ToList();

        // Check every name before writing any file, so a clash cannot overwrite a take that was already saved.
        if (takes.Select(t => t.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != takes.Count)
        {
            throw new ArgumentException("Two takes map to the same file name.", nameof(report));
        }

        foreach ((string fileName, byte[] bytes) in takes)
        {
            await WriteTakeAsync(auditionDir, fileName, bytes, cancellationToken).ConfigureAwait(false);
        }

        string manifestPath = Path.Combine(outputDir, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(ToManifest(report), ManifestJson), cancellationToken).ConfigureAwait(false);
        return manifestPath;
    }

    // The file name comes from AuditionService.TakeFileName, which keeps it inside one folder. The check
    // here is a second guard: the resolved path must stay under the audition directory.
    internal static async Task WriteTakeAsync(string auditionDir, string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(auditionDir) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(auditionDir, fileName));
        if (!target.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Take file name resolves outside the audition directory.", nameof(fileName));
        }

        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
    }

    // A take with no bytes was not written, so the manifest must not name a file for it.
    private static AuditionReport ToManifest(AuditionReport report)
    {
        return report with { Segments = report.Segments.Select(ToManifestSegment).ToArray() };
    }

    private static AuditionSegmentReport ToManifestSegment(AuditionSegmentReport segment)
    {
        AuditionTakeReport[] takes = segment.Takes.Select(ToManifestTake).ToArray();
        AuditionTakeReport? winner = takes.FirstOrDefault(t => t.IsWinner);
        return segment with { Takes = takes, WinnerFileName = winner?.AudioFileName };
    }

    private static AuditionTakeReport ToManifestTake(AuditionTakeReport take)
    {
        return take.AudioBytes is null || string.IsNullOrEmpty(take.AudioFileName)
            ? take with { AudioFileName = null }
            : take with { AudioSha256 = Convert.ToHexString(SHA256.HashData(take.AudioBytes)).ToLowerInvariant() };
    }
}
