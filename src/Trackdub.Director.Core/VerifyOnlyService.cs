using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>Options for <see cref="VerifyOnlyService"/>. <see cref="MediaDir"/> defaults to media/audition inside the run folder.</summary>
public sealed record VerifyOnlyOptions(string? MediaDir = null, double ToleranceFraction = 0.10, string ManifestFileName = AuditionOutputWriter.ManifestFileName);

/// <summary>
/// CI-style gate over a captured audition run, with zero network calls. Every verdict is recomputed from
/// the recorded numbers: the manifest summary fields (cast flag, winner flags, timing verdicts) are never
/// trusted as a pass. A segment is Verified only when it has a winning take, that take re-passes the QA
/// lane duration check against the stamped window, a meaning Pass is recorded for it, and its media file
/// is on disk. Anything else needs a human, with the reasons spelled out.
/// </summary>
public static class VerifyOnlyService
{
    public const string MediaHashSidecarFileName = "audition-media-hashes.json";

    public static VerifyReceipt Verify(string runDir, VerifyOnlyOptions? options = null)
    {
        options ??= new VerifyOnlyOptions();
        if (!double.IsFinite(options.ToleranceFraction) || options.ToleranceFraction < 0)
        {
            throw new VerifyInputException("Tolerance must be a finite, non-negative fraction.");
        }

        if (!Directory.Exists(runDir))
        {
            throw new VerifyInputException($"Run folder not found: {runDir}");
        }

        string manifestPath = Path.Combine(runDir, options.ManifestFileName);
        (AuditionReport report, InputHash manifestHash) = ReadManifest(manifestPath, options.ManifestFileName);
        string mediaDir = options.MediaDir ?? Path.Combine([runDir, .. AuditionOutputWriter.MediaSubfolders]);
        (IReadOnlyDictionary<string, string> sidecar, InputHash? sidecarHash) = ReadSidecar(runDir);
        var context = new Context(mediaDir, options.ToleranceFraction, sidecar);

        SegmentReceipt[] segments = report.Segments.Select(s => VerifySegment(s, context)).ToArray();
        int verified = segments.Count(s => s.Verdict == SegmentVerdict.Verified);
        bool allVerified = verified == segments.Length;
        return new VerifyReceipt(
            VerifyReceipt.SchemaId,
            VerifyReceipt.SealStatement,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDir))),
            options.MediaDir ?? string.Join('/', AuditionOutputWriter.MediaSubfolders),
            options.ToleranceFraction,
            allVerified ? SegmentVerdict.Verified : SegmentVerdict.HumanReviewRequired,
            allVerified ? 0 : 1,
            segments.Length,
            verified,
            report.Cast ?? [],
            sidecarHash is null ? [manifestHash] : [manifestHash, sidecarHash],
            segments);
    }

    private static (AuditionReport Report, InputHash Hash) ReadManifest(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new VerifyInputException($"Manifest not found: {path}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VerifyInputException($"Manifest unreadable: {ex.Message}");
        }

        AuditionReport? report;
        try
        {
            report = JsonSerializer.Deserialize<AuditionReport>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new VerifyInputException($"Manifest is not a valid audition manifest: {ex.Message}");
        }

        ValidateReport(report);
        return (report!, new InputHash(label, HexSha256(bytes), HashLabels.Recomputed));
    }

    // Optional sidecar of media hashes beside the manifest, for runs captured before manifests recorded them.
    // It is a consumed input like the manifest; its hashes are labelled recorded-in-sidecar, never recomputed.
    private static (IReadOnlyDictionary<string, string> Hashes, InputHash? Input) ReadSidecar(string runDir)
    {
        var none = new Dictionary<string, string>();
        string path = Path.Combine(runDir, MediaHashSidecarFileName);
        if (!File.Exists(path))
        {
            return (none, null);
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument doc = JsonDocument.Parse(bytes);
            Dictionary<string, string> files = doc.RootElement.GetProperty("files").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            return (files, new InputHash(MediaHashSidecarFileName, HexSha256(bytes), HashLabels.Recomputed));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new VerifyInputException($"Media hash sidecar unreadable: {ex.Message}");
        }
    }

    private static void ValidateReport(AuditionReport? report)
    {
        if (report?.Segments is not { Length: > 0 })
        {
            throw new VerifyInputException("Manifest has no segments to verify.");
        }

        if (report.Segments.Any(IsMalformed))
        {
            throw new VerifyInputException("Manifest has a segment with no id, no takes list or an invalid window.");
        }
    }

    private static bool IsMalformed(AuditionSegmentReport? segment)
    {
        return segment is null
            || string.IsNullOrWhiteSpace(segment.Id)
            || segment.Takes is null
            || segment.Takes.Any(t => t is null)
            || !double.IsFinite(segment.WindowSeconds)
            || segment.WindowSeconds < 0;
    }

    private static SegmentReceipt VerifySegment(AuditionSegmentReport segment, Context context)
    {
        AuditionTakeReport? winner = FindWinner(segment);
        RefusedTake[] refusals = segment.Takes.Where(t => !ReferenceEquals(t, winner)).Select(t => Refuse(t, segment.WindowSeconds, context)).ToArray();
        if (winner is null)
        {
            return BuildSegment(segment, null, null, null, null, refusals, [VerifyReason.Uncast]);
        }

        TimingCheck timing = CheckTiming(winner.MeasuredSeconds, winner.Synthesized, segment.WindowSeconds, context.Tolerance);
        if (Math.Abs(winner.WindowSeconds - segment.WindowSeconds) > 1e-9)
        {
            // The take was judged against a different window than the stamped one: its recorded timing cannot be trusted.
            timing = timing with { Verdict = AuditionTimingVerdict.Fail };
        }

        var meaning = new MeaningCheck(winner.MeaningVerdict, winner.MeaningExplanation, winner.MeaningIssues);
        MediaCheck media = CheckMedia(winner, context);
        return BuildSegment(segment, winner, timing, meaning, media, refusals, WinnerReasons(timing, meaning, media, winner, context));
    }

    // The winning take is the one the manifest names, but only when the manifest is coherent about it.
    // A missing, ambiguous or self-contradicting winner means the line is uncast.
    private static AuditionTakeReport? FindWinner(AuditionSegmentReport segment)
    {
        if (segment.WinnerTakeNumber is not int number)
        {
            return null;
        }

        AuditionTakeReport[] named = segment.Takes.Where(t => t.TakeNumber == number).ToArray();
        bool singleFlag = segment.Takes.Count(t => t.IsWinner) == 1;
        return singleFlag && named is [{ IsWinner: true } only] && segment.WinnerFileName == only.AudioFileName ? only : null;
    }

    private static SegmentReceipt BuildSegment(
        AuditionSegmentReport segment,
        AuditionTakeReport? winner,
        TimingCheck? timing,
        MeaningCheck? meaning,
        MediaCheck? media,
        RefusedTake[] refusals,
        VerifyReason[] reasons)
    {
        return new SegmentReceipt(
            segment.Id,
            segment.Text,
            winner?.Text,
            segment.WindowSeconds,
            winner is null ? null : segment.CastVoice,
            winner?.TakeNumber,
            timing,
            meaning,
            media,
            refusals,
            reasons.Length == 0 ? SegmentVerdict.Verified : SegmentVerdict.HumanReviewRequired,
            reasons);
    }

    // Reuses the QA lane duration check (DurationEstimator.Fits), the same call the audition lane made when it
    // judged the take, against the stamped window. A take with no usable measurement cannot pass.
    private static TimingCheck CheckTiming(double? measured, bool synthesized, double windowSeconds, double tolerance)
    {
        double allowed = windowSeconds * (1.0 + tolerance);
        bool measurable = synthesized && measured is > 0 && double.IsFinite(measured.Value);
        AuditionTimingVerdict verdict = !measurable ? AuditionTimingVerdict.NotSynthesized
            : DurationEstimator.Fits(measured!.Value, windowSeconds, tolerance) ? AuditionTimingVerdict.Pass
            : AuditionTimingVerdict.Fail;
        return new TimingCheck(measured, windowSeconds, tolerance, allowed, verdict);
    }

    private static VerifyReason[] WinnerReasons(TimingCheck timing, MeaningCheck meaning, MediaCheck media, AuditionTakeReport winner, Context context)
    {
        var reasons = new List<VerifyReason>();
        if (timing.Verdict != AuditionTimingVerdict.Pass)
        {
            reasons.Add(VerifyReason.TimingFail);
        }

        reasons.AddRange(MeaningReasons(meaning.Verdict));
        if (!media.Present)
        {
            reasons.Add(VerifyReason.MediaNotPresent);
        }
        else if (RecordedHash(winner, context) is { } recorded && !string.Equals(recorded, media.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(VerifyReason.MediaHashMismatch);
        }

        return reasons.ToArray();
    }

    private static VerifyReason[] MeaningReasons(MeaningVerdict verdict)
    {
        return verdict switch
        {
            MeaningVerdict.Pass => [],
            MeaningVerdict.NotEvaluated => [VerifyReason.MeaningNotEvaluated],
            _ => [VerifyReason.MeaningFail],
        };
    }

    // A present file is hashed and labelled recomputed. An absent file keeps the recorded hash from the
    // manifest, labelled as recorded, or has no hash at all when the manifest recorded none. Absence is never a pass.
    private static MediaCheck CheckMedia(AuditionTakeReport winner, Context context)
    {
        string? name = winner.AudioFileName;
        string? path = IsPlainFileName(name) ? Path.Combine(context.MediaDir, name!) : null;
        if (path is not null && File.Exists(path))
        {
            return new MediaCheck(name, true, HexSha256File(path), HashLabels.Recomputed);
        }

        string? recorded = RecordedHash(winner, context);
        string label = winner.AudioSha256 is not null ? HashLabels.RecordedAtCapture
            : recorded is not null ? HashLabels.RecordedInSidecar
            : HashLabels.NotRecorded;
        return new MediaCheck(name, false, recorded, label);
    }

    // The manifest hash wins; the sidecar fills in only when the manifest recorded none.
    private static string? RecordedHash(AuditionTakeReport winner, Context context)
    {
        return winner.AudioSha256
            ?? (winner.AudioFileName is { } name && context.Sidecar.TryGetValue(name, out string? hash) ? hash : null);
    }

    private static bool IsPlainFileName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\', ':']) < 0 && name is not ("." or "..");
    }

    private static RefusedTake Refuse(AuditionTakeReport take, double windowSeconds, Context context)
    {
        TimingCheck timing = CheckTiming(take.MeasuredSeconds, take.Synthesized, windowSeconds, context.Tolerance);
        var reasons = MeaningReasons(take.MeaningVerdict).ToList();
        if (take.Synthesized && timing.Verdict != AuditionTimingVerdict.Pass)
        {
            reasons.Add(VerifyReason.TimingFail);
        }

        return new RefusedTake(
            take.TakeNumber,
            take.Kind,
            take.Text,
            take.MeaningVerdict,
            take.MeaningExplanation,
            take.MeasuredSeconds,
            reasons.ToArray(),
            RefusalDetail(take, timing, reasons.Count));
    }

    private static string RefusalDetail(AuditionTakeReport take, TimingCheck timing, int reasonCount)
    {
        if (!take.Synthesized)
        {
            return take.SynthesisNote ?? "Not synthesized.";
        }

        string measured = timing.MeasuredSeconds?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "none";
        string allowed = timing.AllowedSeconds.ToString("0.0000", CultureInfo.InvariantCulture);
        string window = timing.WindowSeconds.ToString("0.####", CultureInfo.InvariantCulture);
        string tolerance = Percent(timing.ToleranceFraction);
        string text = $"Measured {measured} s against {allowed} s allowed (window {window} s, tolerance {tolerance}).";
        return reasonCount == 0 ? $"{text} Passed both gates; not the winning take." : text;
    }

    private static string HexSha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string HexSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Context(string MediaDir, double Tolerance, IReadOnlyDictionary<string, string> Sidecar);
}
