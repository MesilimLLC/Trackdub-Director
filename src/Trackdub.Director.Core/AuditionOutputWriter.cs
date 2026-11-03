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

    public const string SpeedCalibrationFileName = "speed-calibration.json";

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

        // Everything is staged first, so a failure while writing leaves the previous run in place. Publishing
        // then replaces files one at a time (manifest last): that narrows the window for a half-replaced run
        // but is not atomic, and recorded hashes expose any mismatch.
        string stagingDir = Path.Combine(outputDir, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            foreach ((string fileName, byte[] bytes) in takes)
            {
                await WriteTakeAsync(stagingDir, fileName, bytes, cancellationToken).ConfigureAwait(false);
            }

            string stagedManifest = Path.Combine(stagingDir, ManifestFileName);
            await File.WriteAllTextAsync(stagedManifest, JsonSerializer.Serialize(ToManifest(report), ManifestJson), cancellationToken).ConfigureAwait(false);

            // The calibration sidecar is staged first and published before the
            // manifest, so a sidecar read or write failure leaves the previous
            // manifest (and the run's earlier calibration record) in place.
            string? stagedCalibration = await StageSpeedCalibrationAsync(report, outputDir, stagingDir, cancellationToken).ConfigureAwait(false);

            foreach ((string fileName, _) in takes)
            {
                File.Move(Path.Combine(stagingDir, fileName), Path.Combine(auditionDir, fileName), overwrite: true);
            }

            if (stagedCalibration is not null)
            {
                File.Move(stagedCalibration, Path.Combine(outputDir, SpeedCalibrationFileName), overwrite: true);
            }

            string manifestPath = Path.Combine(outputDir, ManifestFileName);
            File.Move(stagedManifest, manifestPath, overwrite: true);
            return manifestPath;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    // Append-only calibration log: this run's samples are appended to any
    // samples already on record in the run folder, never rewritten or dropped.
    // The merged content is written to a file in the staging directory and
    // returned for the caller to move into place, so an interrupted write
    // never leaves partial JSON at the published path. An unreadable existing
    // sidecar is preserved aside (never silently overwritten) and the merge
    // restarts from this run's samples only.
    private static async Task<string?> StageSpeedCalibrationAsync(AuditionReport report, string outputDir, string stagingDir, CancellationToken cancellationToken)
    {
        if (report.SpeedCalibration is not { Length: > 0 } newSamples)
        {
            return null;
        }

        string path = Path.Combine(outputDir, SpeedCalibrationFileName);
        var all = new List<SpeedCalibrationSample>();
        if (File.Exists(path))
        {
            string existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                try
                {
                    all.AddRange(JsonSerializer.Deserialize<SpeedCalibrationSample[]>(existing, ManifestJson) ?? []);
                }
                catch (JsonException)
                {
                    // Preserve the damaged record so it can be inspected, then
                    // start the append from this run's samples only.
                    string aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    File.Move(path, aside, overwrite: true);
                }
            }
        }

        all.AddRange(newSamples);
        string staged = Path.Combine(stagingDir, SpeedCalibrationFileName);
        await File.WriteAllTextAsync(staged, JsonSerializer.Serialize(all.ToArray(), ManifestJson), cancellationToken).ConfigureAwait(false);
        return staged;
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
