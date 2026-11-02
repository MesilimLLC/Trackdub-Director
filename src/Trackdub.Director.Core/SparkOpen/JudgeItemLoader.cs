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
        items.AddRange(await LoadEvaluatedAttemptsAsync(pressureReportPath, PressureSource, cancellationToken).ConfigureAwait(false));
        foreach (string path in uniformReportPaths)
        {
            items.AddRange(await LoadEvaluatedAttemptsAsync(path, UniformSource, cancellationToken).ConfigureAwait(false));
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
    private static async Task<IEnumerable<JudgeItem>> LoadEvaluatedAttemptsAsync(string reportPath, string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        string json = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);

        QaReport? report;
        try
        {
            report = JsonSerializer.Deserialize<QaReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid QA report JSON in '{reportPath}': {ex.Message}", ex);
        }

        if (report is null)
        {
            throw new InvalidOperationException($"Empty or null QA report in '{reportPath}'.");
        }

        if (report.Segments is null)
        {
            throw new InvalidOperationException($"QA report '{reportPath}' has no 'segments' array.");
        }

        var items = new List<JudgeItem>();
        foreach (QaSegmentReport segment in report.Segments)
        {
            if (segment.Revisions is null)
            {
                throw new InvalidOperationException($"QA report '{reportPath}' segment '{segment.Id}' is missing 'revisions'.");
            }

            // A failed generation (generation_error, null first_attempt) was never meaning-evaluated,
            // so it carries no hosted verdict to diff against.
            if (segment.FirstAttempt is null)
            {
                continue;
            }

            foreach (QaAttemptReport attempt in new[] { segment.FirstAttempt! }.Concat(segment.Revisions))
            {
                if (attempt.MeaningVerdict is not (MeaningVerdict.Pass or MeaningVerdict.Fail))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(segment.Text) || string.IsNullOrWhiteSpace(attempt.Translation))
                {
                    throw new InvalidOperationException($"QA report '{reportPath}' segment '{segment.Id}' attempt {attempt.AttemptNumber} has no source text or translation.");
                }

                string id = $"{Path.GetFileNameWithoutExtension(reportPath)}:{segment.Id}:a{attempt.AttemptNumber}";
                items.Add(new JudgeItem(
                    id,
                    source,
                    segment.Text,
                    attempt.Translation,
                    attempt.MeaningVerdict,
                    attempt.MeaningExplanation));
            }
        }

        return items;
    }
}
