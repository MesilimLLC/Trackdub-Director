using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Enumerates the recovery set from captured QA reports and builds each item's
/// leakage-safe input. A segment attempt QUALIFIES when it was meaning-evaluated and
/// failed, OR when the segment exhausted the QA loop still failing (final outcome not a
/// pass); one <see cref="RecoveryItem"/> is produced per qualifying attempt.
///
/// LEAKAGE RULE: an item carries ONLY the source line, its window and tolerance, the
/// recorded gate failure reasons for that one failed attempt, and any captured scene
/// context. It NEVER carries any other attempt's text for the same segment — no later
/// revision, retry, or accepted candidate — and the seeded meaning-eval labels never
/// enter here because this reads the QA REPORTS (gate outputs), never cases.json /
/// pressure-segments.json. The refused candidate's OWN text is included (it is the
/// thing that was refused), but every sibling attempt's text is dropped by construction:
/// each item is built from exactly one attempt record.
/// </summary>
public static class RecoverySetBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Build the recovery set from the given report file paths (in order). Reads each file once.</summary>
    public static async Task<IReadOnlyList<RecoveryItem>> BuildAsync(
        IReadOnlyList<string> reportPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportPaths);
        var items = new List<RecoveryItem>();
        foreach (string path in reportPaths)
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            items.AddRange(FromReportJson(json, SourceFileLabel(path)));
        }

        return items;
    }

    /// <summary>Build items from one report's JSON. <paramref name="sourceFile"/> labels their provenance.</summary>
    public static IReadOnlyList<RecoveryItem> FromReportJson(string reportJson, string sourceFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        QaReport? report;
        try
        {
            report = JsonSerializer.Deserialize<QaReport>(reportJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid QA report JSON for '{sourceFile}': {ex.Message}", ex);
        }

        if (report?.Segments is null)
        {
            throw new InvalidOperationException($"QA report '{sourceFile}' has no segments.");
        }

        var items = new List<RecoveryItem>();
        foreach (QaSegmentReport segment in report.Segments)
        {
            items.AddRange(QualifyingItems(report, segment, sourceFile));
        }

        return items;
    }

    private static IEnumerable<RecoveryItem> QualifyingItems(QaReport report, QaSegmentReport segment, string sourceFile)
    {
        QaAttemptReport[] allAttempts = new[] { segment.FirstAttempt }.Concat(segment.Revisions).ToArray();
        bool segmentExhausted = segment.FinalVerdict != QaVerdict.Pass;

        foreach (QaAttemptReport attempt in allAttempts)
        {
            bool meaningEvaluatedAndFailed = attempt.MeaningVerdict == MeaningVerdict.Fail;

            // Qualify a meaning-failed attempt directly. When the segment exhausted the loop
            // still failing, also qualify its terminal attempts that failed timing (never a
            // pass). A meaning Pass attempt never qualifies.
            bool qualifies = meaningEvaluatedAndFailed
                || (segmentExhausted && attempt.Verdict != QaVerdict.Pass && attempt.MeaningVerdict != MeaningVerdict.Pass);
            if (!qualifies)
            {
                continue;
            }

            yield return BuildItem(report, segment, attempt, sourceFile);
        }
    }

    private static RecoveryItem BuildItem(QaReport report, QaSegmentReport segment, QaAttemptReport attempt, string sourceFile)
    {
        RecoveryFailureType failureType = attempt.TimingVerdict == QaVerdict.Fail
            ? RecoveryFailureType.Timing
            : RecoveryFailureType.Meaning;

        string[] reasons = BuildReasons(attempt, failureType);
        double targetCps = report.QaOptions.TargetCharactersPerSecond > 0
            ? report.QaOptions.TargetCharactersPerSecond
            : DurationEstimator.DefaultCharactersPerSecond;

        return new RecoveryItem(
            SourceFile: sourceFile,
            SegmentId: segment.Id,
            AttemptNumber: attempt.AttemptNumber,
            SourceLine: segment.Text,
            SourceLanguage: report.SourceLanguage,
            TargetLanguage: report.TargetLanguage,
            WindowSeconds: attempt.WindowSeconds,
            ToleranceFraction: report.QaOptions.ToleranceFraction,
            TargetCharactersPerSecond: targetCps,
            FailureType: failureType,
            RefusedCandidate: attempt.Translation,
            GateFailureReasons: reasons,
            SceneContext: null);
    }

    private static string[] BuildReasons(QaAttemptReport attempt, RecoveryFailureType failureType)
    {
        var reasons = new List<string>();
        if (failureType == RecoveryFailureType.Timing)
        {
            reasons.Add($"Timing: estimated {attempt.EstimatedSeconds:0.00}s exceeds the {attempt.WindowSeconds:0.00}s window.");
            return reasons.ToArray();
        }

        // Meaning failure: carry the recorded meaning issues/explanation verbatim. These are
        // gate OUTPUTS for THIS attempt, not any sibling attempt's text.
        if (attempt.MeaningIssues is { Length: > 0 })
        {
            reasons.AddRange(attempt.MeaningIssues);
        }

        if (!string.IsNullOrWhiteSpace(attempt.MeaningExplanation))
        {
            reasons.Add(attempt.MeaningExplanation!);
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Meaning gate failed (no specific reasons recorded).");
        }

        return reasons.ToArray();
    }

    private static string SourceFileLabel(string path)
    {
        // Keep a readable, stable label: the last two path segments (folder/file).
        string file = Path.GetFileName(path);
        string? dir = Path.GetDirectoryName(path);
        string folder = dir is null ? string.Empty : Path.GetFileName(dir);
        return string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";
    }
}
