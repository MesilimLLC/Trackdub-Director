using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Assembles the 75 judge items from the project's captured evaluation sets on disk, each item
/// carrying the hosted verdict already recorded for it:
///   • 40 labeled eval cases from meaning-eval/cases.json (hosted verdict = the labeled expectation
///     the hosted gate matched in meaning-eval-report.json);
///   • 23 pressure candidates from meaning-eval/qa-report-pressure-70.json — every attempt the hosted
///     gate actually meaning-evaluated (verdict Pass or Fail; timing-only failures where meaning was
///     NotEvaluated are excluded);
///   • 12 uniform candidates from voa10-scale/qa-report-scale.json and qa-report-voa10.json — every
///     attempt the hosted gate meaning-evaluated across the two uniform-config runs.
/// These are candidates the hosted model already judged, which is exactly what the independent judge
/// needs to diff against.
/// </summary>
public static class JudgeItemLoader
{
    public const string CasesSource = "cases";
    public const string PressureSource = "pressure";
    public const string UniformSource = "uniform";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Loads and concatenates the three sets from the given sample roots. Paths are explicit so a
    /// caller (CLI or test) controls exactly which files are read.
    /// </summary>
    public static async Task<IReadOnlyList<JudgeItem>> LoadAsync(
        string casesPath,
        string pressureReportPath,
        IReadOnlyList<string> uniformReportPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pressureReportPath);
        ArgumentNullException.ThrowIfNull(uniformReportPaths);

        if (uniformReportPaths.Count != uniformReportPaths.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("Uniform report paths must be unique; a repeated path judges the same candidates twice.");
        }

        var items = new List<JudgeItem>();
        items.AddRange(await LoadCasesAsync(casesPath, cancellationToken).ConfigureAwait(false));
        items.AddRange(await LoadEvaluatedAttemptsAsync(new ReportPath(pressureReportPath), new ItemSource(PressureSource), cancellationToken).ConfigureAwait(false));
        foreach (string path in uniformReportPaths)
        {
            items.AddRange(await LoadEvaluatedAttemptsAsync(new ReportPath(path), new ItemSource(UniformSource), cancellationToken).ConfigureAwait(false));
        }

        return items;
    }

    private static async Task<IEnumerable<JudgeItem>> LoadCasesAsync(string casesPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<MeaningEvalCase> cases = await MeaningEvalService.LoadCasesAsync(casesPath, cancellationToken).ConfigureAwait(false);
        return cases.Select(c => new JudgeItem(
            c.Id, CasesSource, c.SourceText, c.Candidate, c.Expected, c.Note));
    }

    /// <summary>
    /// Reads a QA report and yields one judge item per attempt the hosted gate meaning-evaluated
    /// (meaning_verdict Pass or Fail). Timing-only failures (NotEvaluated) and errors are skipped —
    /// they carry no hosted meaning verdict to diff against.
    /// </summary>
    private static async Task<IEnumerable<JudgeItem>> LoadEvaluatedAttemptsAsync(ReportPath reportPath, ItemSource source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath.Value);
        string json = await File.ReadAllTextAsync(reportPath.Value, cancellationToken).ConfigureAwait(false);
        QaReport report = ParseReport(new ReportJson(json), reportPath);
        return EnumerateItems(report, reportPath, source);
    }

    private static QaReport ParseReport(ReportJson json, ReportPath reportPath)
    {
        QaReport? report;
        try
        {
            report = JsonSerializer.Deserialize<QaReport>(json.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid QA report JSON in '{reportPath.Value}': {ex.Message}", ex);
        }

        if (report is null)
        {
            throw new InvalidOperationException($"Empty or null QA report in '{reportPath.Value}'.");
        }

        if (report.Segments is null)
        {
            throw new InvalidOperationException($"QA report '{reportPath.Value}' has no 'segments' array.");
        }

        return report;
    }

    private static IEnumerable<JudgeItem> EnumerateItems(QaReport report, ReportPath reportPath, ItemSource source)
    {
        var items = new List<JudgeItem>();
        foreach (QaSegmentReport segment in report.Segments)
        {
            AddSegmentItems(segment, reportPath, source, items);
        }

        return items;
    }

    private static void AddSegmentItems(QaSegmentReport segment, ReportPath reportPath, ItemSource source, List<JudgeItem> items)
    {
        if (segment.Revisions is null)
        {
            throw new InvalidOperationException($"QA report '{reportPath.Value}' segment '{segment.Id}' is missing 'revisions'.");
        }

        // A failed generation (generation_error, null first_attempt) was never meaning-evaluated,
        // so it carries no hosted verdict to diff against.
        if (segment.FirstAttempt is null)
        {
            return;
        }

        foreach (QaAttemptReport attempt in AllAttempts(segment))
        {
            if (IsMeaningEvaluated(attempt))
            {
                items.Add(BuildItem(segment, attempt, reportPath, source));
            }
        }
    }

    private static IEnumerable<QaAttemptReport> AllAttempts(QaSegmentReport segment)
        => new[] { segment.FirstAttempt! }.Concat(segment.Revisions);

    private static bool IsMeaningEvaluated(QaAttemptReport attempt)
        => attempt.MeaningVerdict is MeaningVerdict.Pass or MeaningVerdict.Fail;

    private static JudgeItem BuildItem(QaSegmentReport segment, QaAttemptReport attempt, ReportPath reportPath, ItemSource source)
    {
        if (string.IsNullOrWhiteSpace(segment.Text) || string.IsNullOrWhiteSpace(attempt.Translation))
        {
            throw new InvalidOperationException($"QA report '{reportPath.Value}' segment '{segment.Id}' attempt {attempt.AttemptNumber} has no source text or translation.");
        }

        string id = $"{Path.GetFileNameWithoutExtension(reportPath.Value)}:{segment.Id}:a{attempt.AttemptNumber}";
        return new JudgeItem(
            id,
            source.Value,
            segment.Text,
            attempt.Translation,
            attempt.MeaningVerdict,
            attempt.MeaningExplanation);
    }

    private readonly record struct ReportJson(string Value);

    private readonly record struct ReportPath(string Value);

    private readonly record struct ItemSource(string Value);
}
