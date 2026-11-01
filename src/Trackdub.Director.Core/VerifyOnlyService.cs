using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Offline gate over a captured run's manifests. Recomputes verdicts from
/// recorded numbers; never trusts summary fields; a check that was not run
/// never counts as a pass. Zero live calls.
/// </summary>
public sealed class VerifyOnlyService
{
    public const string ReceiptSchema = "trackdub.verify-only.receipt.v1";
    public const string DefaultRunRelativePath = "samples/voa10";
    public const string AuditionManifestFileName = "audition-manifest.json";
    public const string MediaHashesFileName = "audition-media-hashes.json";
    public const string DefaultReceiptFileName = "verify-only-receipt.json";
    public const string DefaultReceiptHtmlFileName = "verify-only-receipt.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly double _toleranceFraction;

    public VerifyOnlyService(double? toleranceFraction = null)
    {
        double tolerance = toleranceFraction ?? QaOptions.Default.ToleranceFraction;
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceFraction), "Tolerance must be non-negative.");
        }

        _toleranceFraction = tolerance;
    }

    public VerifyOnlyResult VerifyRun(string runDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDir);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(runDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Run directory is not a valid path: {runDir}", ex);
        }

        if (!Directory.Exists(resolved))
        {
            throw new InvalidOperationException($"Run directory not found: {resolved}");
        }

        string manifestPath = Path.Combine(resolved, AuditionManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Audition manifest not found: {manifestPath}");
        }

        AuditionReport report = LoadManifest(manifestPath);
        IReadOnlyDictionary<string, string> recordedMediaHashes = LoadRecordedMediaHashes(resolved);

        var inputHashes = new List<VerifyOnlyHashEntry>
        {
            HashFile(manifestPath, resolved, VerifyOnlyHashProvenance.Recomputed),
        };

        string segmentsPath = Path.Combine(resolved, "segments.json");
        if (File.Exists(segmentsPath))
        {
            inputHashes.Add(HashFile(segmentsPath, resolved, VerifyOnlyHashProvenance.Recomputed));
        }

        string hashesPath = Path.Combine(resolved, MediaHashesFileName);
        if (File.Exists(hashesPath))
        {
            inputHashes.Add(HashFile(hashesPath, resolved, VerifyOnlyHashProvenance.Recomputed));
        }

        var segments = new List<VerifyOnlySegmentReceipt>(report.Segments.Length);
        foreach (AuditionSegmentReport segment in report.Segments)
        {
            segments.Add(VerifySegment(segment, resolved, recordedMediaHashes));
        }

        bool allVerified = segments.Count > 0 && segments.All(s => s.Verdict == VerifyOnlySegmentVerdict.Verified);
        var overall = allVerified ? VerifyOnlySegmentVerdict.Verified : VerifyOnlySegmentVerdict.HumanReviewRequired;
        int exitStatus = allVerified ? 0 : 1;

        var receipt = new VerifyOnlyReceipt(
            Schema: ReceiptSchema,
            RunDir: DisplayRunDir(resolved),
            OverallVerdict: overall,
            ExitStatus: exitStatus,
            ToleranceFraction: _toleranceFraction,
            InputHashes: inputHashes.ToArray(),
            Segments: segments.ToArray(),
            Notes: "Hash-sealed verify-only receipt (not a signature / certificate / attestation). A check that was not run never counts as a pass.");

        return new VerifyOnlyResult(receipt, exitStatus);
    }

    private static string DisplayRunDir(string resolved)
    {
        try
        {
            string cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
            string relative = Path.GetRelativePath(cwd, resolved);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Fall through to the absolute path.
        }

        return resolved;
    }

    public static async Task WriteReceiptAsync(string path, VerifyOnlyReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(receipt);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(receipt, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteReceiptHtmlAsync(string path, VerifyOnlyReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(receipt);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string html = VerifyOnlyHtml.Render(receipt);
        await File.WriteAllTextAsync(path, html, cancellationToken).ConfigureAwait(false);
    }

    public static VerifyOnlyReceipt LoadReceipt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path);
        VerifyOnlyReceipt? receipt = JsonSerializer.Deserialize<VerifyOnlyReceipt>(json, JsonOptions);
        if (receipt is null)
        {
            throw new InvalidOperationException($"Invalid verify-only receipt JSON: {path}");
        }

        return receipt;
    }

    private VerifyOnlySegmentReceipt VerifySegment(
        AuditionSegmentReport segment,
        string runDir,
        IReadOnlyDictionary<string, string> recordedMediaHashes)
    {
        var reasons = new List<VerifyOnlyReason>();
        AuditionTakeReport? winner = FindWinner(segment);
        VerifyOnlyRefusal[] refusals = BuildRefusals(segment, winner);

        if (winner is null)
        {
            reasons.Add(VerifyOnlyReason.Uncast);
            return BuildSegment(
                segment,
                spanish: null,
                measured: null,
                timingPass: null,
                meaning: null,
                explanation: null,
                voice: segment.CastVoice,
                winnerFile: null,
                mediaHash: null,
                mediaPresent: false,
                refusals,
                reasons);
        }

        string? spanish = winner.Text;
        double? measured = winner.MeasuredSeconds;
        bool? timingPass = null;
        if (measured is null)
        {
            reasons.Add(VerifyOnlyReason.MeasuredDurationMissing);
        }
        else
        {
            timingPass = DurationEstimator.Fits(measured.Value, segment.WindowSeconds, _toleranceFraction);
            if (!timingPass.Value)
            {
                reasons.Add(VerifyOnlyReason.TimingFail);
            }
        }

        MeaningVerdict meaning = winner.MeaningVerdict;
        if (meaning == MeaningVerdict.Pass)
        {
            // Pass recorded for the winning text.
        }
        else if (meaning == MeaningVerdict.NotEvaluated)
        {
            reasons.Add(VerifyOnlyReason.MeaningNotEvaluated);
        }
        else
        {
            reasons.Add(VerifyOnlyReason.MeaningFail);
        }

        string? winnerFile = winner.AudioFileName ?? segment.WinnerFileName;
        bool mediaPresent = false;
        VerifyOnlyHashEntry? mediaHash = null;
        if (string.IsNullOrWhiteSpace(winnerFile))
        {
            reasons.Add(VerifyOnlyReason.MediaNotPresent);
            mediaHash = RecordedOrEmpty(winnerFile, recordedMediaHashes);
        }
        else
        {
            string mediaPath = Path.Combine(runDir, "media", "audition", winnerFile);
            if (File.Exists(mediaPath))
            {
                mediaPresent = true;
                string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mediaPath))).ToLowerInvariant();
                mediaHash = new VerifyOnlyHashEntry(
                    Path: ToRelative(mediaPath, runDir),
                    Sha256: sha,
                    Provenance: VerifyOnlyHashProvenance.Recomputed);
            }
            else
            {
                reasons.Add(VerifyOnlyReason.MediaNotPresent);
                mediaHash = RecordedMediaHash(winnerFile, recordedMediaHashes);
            }
        }

        return BuildSegment(
            segment,
            spanish,
            measured,
            timingPass,
            meaning,
            winner.MeaningExplanation,
            segment.CastVoice,
            winnerFile,
            mediaHash,
            mediaPresent,
            refusals,
            reasons);
    }

    private VerifyOnlySegmentReceipt BuildSegment(
        AuditionSegmentReport segment,
        string? spanish,
        double? measured,
        bool? timingPass,
        MeaningVerdict? meaning,
        string? explanation,
        AuditionCastVoice? voice,
        string? winnerFile,
        VerifyOnlyHashEntry? mediaHash,
        bool mediaPresent,
        VerifyOnlyRefusal[] refusals,
        List<VerifyOnlyReason> reasons)
    {
        var verdict = reasons.Count == 0
            ? VerifyOnlySegmentVerdict.Verified
            : VerifyOnlySegmentVerdict.HumanReviewRequired;

        return new VerifyOnlySegmentReceipt(
            Id: segment.Id,
            SourceText: segment.Text,
            SpanishText: spanish,
            MeasuredSeconds: measured,
            WindowSeconds: segment.WindowSeconds,
            TimingRecomputedPass: timingPass,
            MeaningVerdict: meaning,
            MeaningExplanation: explanation,
            WinningVoice: voice,
            WinnerFileName: winnerFile,
            WinningMediaHash: mediaHash,
            MediaPresent: mediaPresent,
            Refusals: refusals,
            Verdict: verdict,
            Reasons: reasons.ToArray());
    }

    private VerifyOnlyRefusal[] BuildRefusals(AuditionSegmentReport segment, AuditionTakeReport? winner)
    {
        var list = new List<VerifyOnlyRefusal>();
        foreach (AuditionTakeReport take in segment.Takes)
        {
            if (winner is not null && take.TakeNumber == winner.TakeNumber && take.IsWinner)
            {
                continue;
            }

            if (!take.Synthesized && take.MeaningVerdict == MeaningVerdict.Pass && take.TimingVerdict == AuditionTimingVerdict.NotSynthesized)
            {
                // Skipped synthesis still surfaces as a refusal of the candidate line.
            }

            var reasons = new List<string>();
            bool? timingPass = null;
            if (take.Synthesized && take.MeasuredSeconds is double measured)
            {
                timingPass = DurationEstimator.Fits(measured, take.WindowSeconds, _toleranceFraction);
                if (!timingPass.Value)
                {
                    reasons.Add("TimingFail");
                }
            }
            else if (take.Synthesized)
            {
                reasons.Add("MeasuredDurationMissing");
            }
            else
            {
                reasons.Add(string.IsNullOrWhiteSpace(take.SynthesisNote) ? "NotSynthesized" : take.SynthesisNote);
            }

            if (take.MeaningVerdict == MeaningVerdict.Fail || take.MeaningVerdict == MeaningVerdict.Error)
            {
                reasons.Add("MeaningFail");
            }
            else if (take.MeaningVerdict == MeaningVerdict.NotEvaluated)
            {
                reasons.Add("MeaningNotEvaluated");
            }

            if (take.IsWinner)
            {
                continue;
            }

            if (reasons.Count == 0 && take.MeaningVerdict == MeaningVerdict.Pass && timingPass == true)
            {
                // Meaning+timing pass but lost the closest-fit pick.
                reasons.Add("NotSelected");
            }

            if (reasons.Count == 0)
            {
                reasons.Add("Refused");
            }

            list.Add(new VerifyOnlyRefusal(
                TakeNumber: take.TakeNumber,
                Text: take.Text,
                Reasons: reasons.ToArray(),
                MeaningVerdict: take.MeaningVerdict,
                MeaningExplanation: take.MeaningExplanation,
                MeasuredSeconds: take.MeasuredSeconds,
                TimingRecomputedPass: timingPass));
        }

        return list.ToArray();
    }

    private static AuditionTakeReport? FindWinner(AuditionSegmentReport segment)
    {
        // Never trust the cast/summary flags alone: a winner is a take marked
        // is_winner with synthesized audio metadata, or the take_number named
        // by winner_take_number when present.
        AuditionTakeReport? byFlag = segment.Takes.FirstOrDefault(t => t.IsWinner);
        if (byFlag is not null)
        {
            return byFlag;
        }

        if (segment.WinnerTakeNumber is int n)
        {
            return segment.Takes.FirstOrDefault(t => t.TakeNumber == n);
        }

        return null;
    }

    private static AuditionReport LoadManifest(string path)
    {
        string json = File.ReadAllText(path);
        AuditionReport? report;
        try
        {
            report = JsonSerializer.Deserialize<AuditionReport>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid audition manifest JSON: {path}", ex);
        }

        if (report is null || report.Segments is null || report.Segments.Length == 0)
        {
            throw new InvalidOperationException($"Audition manifest has no segments: {path}");
        }

        return report;
    }

    private static IReadOnlyDictionary<string, string> LoadRecordedMediaHashes(string runDir)
    {
        string path = Path.Combine(runDir, MediaHashesFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? sha = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(sha))
                        {
                            map[prop.Name] = sha.Trim().ToLowerInvariant();
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("sha256", out JsonElement shaEl)
                        && shaEl.ValueKind == JsonValueKind.String)
                    {
                        string? sha = shaEl.GetString();
                        if (!string.IsNullOrWhiteSpace(sha))
                        {
                            map[prop.Name] = sha.Trim().ToLowerInvariant();
                        }
                    }
                }
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static VerifyOnlyHashEntry RecordedMediaHash(string fileName, IReadOnlyDictionary<string, string> recorded)
    {
        recorded.TryGetValue(fileName, out string? sha);
        return new VerifyOnlyHashEntry(
            Path: Path.Combine("media", "audition", fileName).Replace('\\', '/'),
            Sha256: sha,
            Provenance: VerifyOnlyHashProvenance.RecordedAtCapture);
    }

    private static VerifyOnlyHashEntry RecordedOrEmpty(string? fileName, IReadOnlyDictionary<string, string> recorded)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new VerifyOnlyHashEntry(
                Path: "media/audition/(missing)",
                Sha256: null,
                Provenance: VerifyOnlyHashProvenance.RecordedAtCapture);
        }

        return RecordedMediaHash(fileName, recorded);
    }

    private static VerifyOnlyHashEntry HashFile(string absolutePath, string runDir, VerifyOnlyHashProvenance provenance)
    {
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant();
        return new VerifyOnlyHashEntry(
            Path: ToRelative(absolutePath, runDir),
            Sha256: sha,
            Provenance: provenance);
    }

    private static string ToRelative(string absolutePath, string runDir)
    {
        string relative = Path.GetRelativePath(runDir, absolutePath);
        return relative.Replace('\\', '/');
    }
}
