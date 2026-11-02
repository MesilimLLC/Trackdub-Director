using System.Text.Json;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Runs Spark Open as the translation provider over the 40 labeled eval source lines, then grades each
/// result with the EXISTING deterministic machinery only: <see cref="DurationEstimator"/> for timing
/// and the hosted <see cref="MeaningGate"/> (muse-spark-1.1, meaning-v1) via
/// <see cref="MetaClient.CheckMeaningAsync"/> for meaning. The open-model output is judged by exactly
/// the same gates that grade the hosted model's, and the result is assembled side-by-side with the
/// hosted lane's recorded translation on the same line.
///
/// Translation uses Spark Open; meaning grading uses the HOSTED gate, by design — the gate is held
/// fixed so the only variable is the translation provider.
/// </summary>
public sealed class SparkOpenTranslationRunner
{
    public const string Stage = "spark-open-translation";

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ISparkOpenClient _sparkOpen;
    private readonly MetaClient _hostedMeaningClient;
    private readonly QaOptions _options;

    /// <param name="sparkOpen">The Spark Open provider that produces the translations.</param>
    /// <param name="hostedMeaningClient">The hosted client whose <see cref="MetaClient.CheckMeaningAsync"/> grades meaning.</param>
    public SparkOpenTranslationRunner(ISparkOpenClient sparkOpen, MetaClient hostedMeaningClient, QaOptions? options = null)
    {
        _sparkOpen = sparkOpen ?? throw new ArgumentNullException(nameof(sparkOpen));
        _hostedMeaningClient = hostedMeaningClient ?? throw new ArgumentNullException(nameof(hostedMeaningClient));
        _options = options ?? QaOptions.Default;
    }

    public async Task<SparkOpenTranslationReport> RunAsync(
        IReadOnlyList<QaSegmentInput> segments,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyDictionary<string, string>? hostedTranslations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        MetaClient translator = _sparkOpen.AsMetaClient();
        double targetCps = _options.GetCharactersPerSecond(targetLanguage);
        var results = new List<SparkOpenTranslationLineResult>(segments.Count);

        foreach (QaSegmentInput segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double windowSeconds = QaService.ResolveWindowSeconds(segment, _options, sourceLanguage);
            int maxChars = Math.Max(1, DurationEstimator.MaxCharsForWindow(windowSeconds * (1.0 + _options.ToleranceFraction), targetCps));
            string? hostedTranslation = hostedTranslations is not null && hostedTranslations.TryGetValue(segment.Id, out string? h) ? h : null;

            try
            {
                MetaClient.ChatCompletionResult translation = await translator.TranslateWithUsageAsync(
                    new MetaClient.TranslationRequest(segment.Text, sourceLanguage, targetLanguage), cancellationToken).ConfigureAwait(false);

                double estimated = DurationEstimator.EstimateSeconds(translation.Content, targetCps);
                bool fits = DurationEstimator.Fits(estimated, windowSeconds, _options.ToleranceFraction);

                MeaningCheckResult meaning = await GradeMeaningAsync(segment.Text, translation.Content, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);

                QaVerdict timingVerdict = fits ? QaVerdict.Pass : QaVerdict.Fail;
                QaVerdict combined = timingVerdict == QaVerdict.Pass && meaning.Verdict == MeaningVerdict.Pass ? QaVerdict.Pass : QaVerdict.Fail;

                results.Add(new SparkOpenTranslationLineResult(
                    segment.Id,
                    segment.Text,
                    windowSeconds,
                    translation.Content,
                    estimated,
                    maxChars,
                    timingVerdict,
                    meaning.Verdict,
                    meaning.Issues,
                    meaning.Explanation,
                    meaning.ModelId,
                    combined,
                    translation.Usage,
                    meaning.Usage,
                    hostedTranslation));
            }
            catch (Exception ex) when (
                ex is MetaApiException or SparkOpenException or HttpRequestException or InvalidOperationException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A failed generation must not erase the run's evidence: record the line as failed with
                // the billed usage (when the failure contract carried it) and continue to the next line.
                results.Add(new SparkOpenTranslationLineResult(
                    segment.Id,
                    segment.Text,
                    windowSeconds,
                    SparkOpenTranslation: string.Empty,
                    EstimatedSeconds: 0,
                    MaxChars: maxChars,
                    TimingVerdict: QaVerdict.Fail,
                    MeaningVerdict: MeaningVerdict.NotEvaluated,
                    MeaningIssues: null,
                    MeaningExplanation: null,
                    MeaningModelId: null,
                    CombinedVerdict: QaVerdict.Fail,
                    SparkOpenTranslationUsage: (ex as MetaApiException)?.Usage,
                    HostedMeaningUsage: null,
                    HostedTranslation: hostedTranslation,
                    TranslationError: ex.Message));
            }
        }

        return new SparkOpenTranslationReport(
            Stage,
            nameof(TextProvider.SparkOpen),
            nameof(TextProvider.Hosted),
            _sparkOpen.Model,
            _sparkOpen.EndpointHost,
            MeaningGate.PromptTemplateVersion,
            sourceLanguage,
            targetLanguage,
            _options.ToleranceFraction,
            targetCps,
            DateTime.UtcNow.ToString("O"),
            Summarize(results),
            results.ToArray());
    }

    private async Task<MeaningCheckResult> GradeMeaningAsync(string sourceText, string candidate, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        try
        {
            return await _hostedMeaningClient.CheckMeaningAsync(
                new MetaClient.MeaningCheckRequest(sourceText, candidate, sourceLanguage, targetLanguage), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return MeaningGate.ForError(ex.Message, MetaAuth.GetModel(), (ex as MetaApiException)?.Usage, HostedHost());
        }
    }

    private static string HostedHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    public static SparkOpenTranslationSummary Summarize(IReadOnlyList<SparkOpenTranslationLineResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int lines = results.Count;
        int timingPass = results.Count(r => r.TimingVerdict == QaVerdict.Pass);
        int meaningPass = results.Count(r => r.MeaningVerdict == MeaningVerdict.Pass);
        int combinedPass = results.Count(r => r.CombinedVerdict == QaVerdict.Pass);
        int meaningErrors = results.Count(r => r.MeaningVerdict == MeaningVerdict.Error);
        int translationErrors = results.Count(r => r.TranslationError is not null);

        return new SparkOpenTranslationSummary(
            lines,
            timingPass,
            meaningPass,
            combinedPass,
            meaningErrors,
            translationErrors,
            Ratio(timingPass, lines),
            Ratio(meaningPass, lines),
            Ratio(combinedPass, lines),
            results.Sum(r => r.SparkOpenTranslationUsage?.TotalTokens ?? 0),
            results.Sum(r => r.HostedMeaningUsage?.TotalTokens ?? 0));
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : Math.Round((double)numerator / denominator, 4);

    public async Task WriteReportAsync(string outputPath, SparkOpenTranslationReport report, CancellationToken cancellationToken = default)
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
