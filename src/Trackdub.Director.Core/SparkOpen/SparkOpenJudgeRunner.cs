using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Runs Spark Open as an independent meaning judge over candidates the hosted model already judged,
/// and diffs the two verdicts. This answers the standing criticism that in this app Spark grades
/// Spark: here a second, open-weight model judges the same candidates with the same meaning-v1
/// criteria, and agreement is reported honestly.
///
/// Agreement is computed ONLY over items both models actually judged. A Spark Open judgement that is
/// <see cref="MeaningVerdict.Error"/> or unparseable, or an item whose hosted verdict is not a clean
/// Pass/Fail, is excluded and counted — never treated as agreement.
/// </summary>
public sealed class SparkOpenJudgeRunner
{
    public const string Stage = "spark-open-judge";

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ISparkOpenClient _client;

    public SparkOpenJudgeRunner(ISparkOpenClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<JudgeAgreementReport> RunAsync(
        IReadOnlyList<JudgeItem> items,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var results = new List<JudgeItemResult>(items.Count);
        foreach (JudgeItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeaningCheckResult judged = await _client.JudgeMeaningAsync(
                new JudgeMeaningRequest(item.SourceText, item.Candidate, sourceLanguage, targetLanguage), cancellationToken).ConfigureAwait(false);
            results.Add(Compare(item, judged));
        }

        return new JudgeAgreementReport(
            Stage,
            Provider: nameof(TextProvider.SparkOpen),
            SparkOpenModel: _client.Model,
            SparkOpenEndpointHost: _client.EndpointHost,
            CriteriaVersion: MeaningGate.PromptTemplateVersion,
            SourceLanguage: sourceLanguage,
            TargetLanguage: targetLanguage,
            TimestampUtc: DateTime.UtcNow.ToString("O"),
            Summary: Summarize(results),
            Results: results.ToArray());
    }

    /// <summary>Classifies one item's two verdicts. Only a clean hosted Pass/Fail against a clean Spark Open Pass/Fail is comparable.</summary>
    public static JudgeItemResult Compare(JudgeItem item, MeaningCheckResult judged)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(judged);

        bool hostedClean = item.HostedVerdict is MeaningVerdict.Pass or MeaningVerdict.Fail;
        bool sparkClean = judged.Verdict is MeaningVerdict.Pass or MeaningVerdict.Fail;

        JudgeComparison comparison = (hostedClean, sparkClean) switch
        {
            (true, true) => item.HostedVerdict == judged.Verdict ? JudgeComparison.Agree : JudgeComparison.Disagree,
            _ => JudgeComparison.Excluded,
        };

        return new JudgeItemResult(
            item.Id,
            item.Source,
            item.SourceText,
            item.Candidate,
            item.HostedVerdict,
            item.HostedReason,
            judged.Verdict,
            judged.Explanation,
            comparison,
            judged.Usage);
    }

    public static JudgeAgreementSummary Summarize(IReadOnlyList<JudgeItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int agree = results.Count(r => r.Comparison == JudgeComparison.Agree);
        int disagree = results.Count(r => r.Comparison == JudgeComparison.Disagree);
        int excluded = results.Count(r => r.Comparison == JudgeComparison.Excluded);
        int compared = agree + disagree;

        return new JudgeAgreementSummary(
            TotalItems: results.Count,
            Compared: compared,
            Agree: agree,
            Disagree: disagree,
            Excluded: excluded,
            AgreementRate: compared == 0 ? null : Math.Round((double)agree / compared, 4),
            TotalTokens: results.Sum(r => r.SparkOpenUsage?.TotalTokens ?? 0));
    }

    public async Task WriteReportAsync(string outputPath, JudgeAgreementReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, WriteOptions), cancellationToken).ConfigureAwait(false);
    }
}
