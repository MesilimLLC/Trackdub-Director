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

    public async Task<SparkOpenTranslationReport> RunAsync(TranslationRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);

        if (request.Segments.Count == 0)
        {
            throw new InvalidOperationException("At least one segment is required for the Spark Open translation lane.");
        }

        var context = new TranslationContext(
            _sparkOpen.AsMetaClient(),
            new TranslationSettings(
                _options.GetCharactersPerSecond(request.TargetLanguage),
                new LanguagePair(request.SourceLanguage, request.TargetLanguage)),
            request.HostedTranslations);

        var results = new List<SparkOpenTranslationLineResult>(request.Segments.Count);
        foreach (QaSegmentInput segment in request.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TranslateSegmentAsync(segment, context, cancellationToken).ConfigureAwait(false));
        }

        return BuildReport(request, context, results);
    }

    private async Task<SparkOpenTranslationLineResult> TranslateSegmentAsync(QaSegmentInput segment, TranslationContext context, CancellationToken cancellationToken)
    {
        SegmentPlan plan = BuildPlan(segment, context);
        try
        {
            return await TranslateAndGradeAsync(plan, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is MetaApiException or SparkOpenException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A failed generation must not erase the run's evidence: record the line as failed with
            // the billed usage (when the failure contract carried it) and continue to the next line.
            return FailedResult(plan, ex);
        }
    }

    private SegmentPlan BuildPlan(QaSegmentInput segment, TranslationContext context)
    {
        double windowSeconds = QaService.ResolveWindowSeconds(segment, _options, context.Settings.Languages.Source);
        int maxChars = Math.Max(1, DurationEstimator.MaxCharsForWindow(windowSeconds * (1.0 + _options.ToleranceFraction), context.Settings.TargetCps));
        return new SegmentPlan(segment, windowSeconds, maxChars, context.HostedTranslation(segment.Id));
    }

    private async Task<SparkOpenTranslationLineResult> TranslateAndGradeAsync(SegmentPlan plan, TranslationContext context, CancellationToken cancellationToken)
    {
        MetaClient.ChatCompletionResult translation = await context.Translator.TranslateWithUsageAsync(
            new MetaClient.TranslationRequest(plan.Segment.Text, context.Settings.Languages.Source, context.Settings.Languages.Target), cancellationToken).ConfigureAwait(false);

        double estimated = DurationEstimator.EstimateSeconds(translation.Content, context.Settings.TargetCps);
        bool fits = DurationEstimator.Fits(estimated, plan.WindowSeconds, _options.ToleranceFraction);

        MeaningCheckResult meaning = await GradeMeaningAsync(
            new MetaClient.MeaningCheckRequest(plan.Segment.Text, translation.Content, context.Settings.Languages.Source, context.Settings.Languages.Target),
            cancellationToken).ConfigureAwait(false);

        QaVerdict timingVerdict = fits ? QaVerdict.Pass : QaVerdict.Fail;
        QaVerdict combined = timingVerdict == QaVerdict.Pass && meaning.Verdict == MeaningVerdict.Pass ? QaVerdict.Pass : QaVerdict.Fail;

        return new SparkOpenTranslationLineResult(
            plan.Segment.Id,
            plan.Segment.Text,
            plan.WindowSeconds,
            translation.Content,
            estimated,
            plan.MaxChars,
            timingVerdict,
            meaning.Verdict,
            meaning.Issues,
            meaning.Explanation,
            meaning.ModelId,
            combined,
            translation.Usage,
            meaning.Usage,
            plan.HostedTranslation);
    }

    private static SparkOpenTranslationLineResult FailedResult(SegmentPlan plan, Exception ex)
    {
        return new SparkOpenTranslationLineResult(
            plan.Segment.Id,
            plan.Segment.Text,
            plan.WindowSeconds,
            SparkOpenTranslation: string.Empty,
            EstimatedSeconds: 0,
            MaxChars: plan.MaxChars,
            TimingVerdict: QaVerdict.Fail,
            MeaningVerdict: MeaningVerdict.NotEvaluated,
            MeaningIssues: null,
            MeaningExplanation: null,
            MeaningModelId: null,
            CombinedVerdict: QaVerdict.Fail,
            SparkOpenTranslationUsage: (ex as MetaApiException)?.Usage,
            HostedMeaningUsage: null,
            HostedTranslation: plan.HostedTranslation,
            TranslationError: ex.Message);
    }

    private async Task<MeaningCheckResult> GradeMeaningAsync(MetaClient.MeaningCheckRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _hostedMeaningClient.CheckMeaningAsync(request, cancellationToken).ConfigureAwait(false);
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

    private SparkOpenTranslationReport BuildReport(TranslationRunRequest request, TranslationContext context, IReadOnlyList<SparkOpenTranslationLineResult> results)
    {
        return new SparkOpenTranslationReport(
            Stage,
            nameof(TextProvider.SparkOpen),
            nameof(TextProvider.Hosted),
            _sparkOpen.Model,
            _sparkOpen.EndpointHost,
            MeaningGate.PromptTemplateVersion,
            request.SourceLanguage,
            request.TargetLanguage,
            _options.ToleranceFraction,
            context.Settings.TargetCps,
            DateTime.UtcNow.ToString("O"),
            Summarize(results),
            results.ToArray());
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

    private sealed record TranslationContext(
        MetaClient Translator,
        TranslationSettings Settings,
        IReadOnlyDictionary<string, string>? HostedTranslations)
    {
        public string? HostedTranslation(string segmentId)
            => HostedTranslations is not null && HostedTranslations.TryGetValue(segmentId, out string? value) ? value : null;
    }

    private readonly record struct TranslationSettings(double TargetCps, LanguagePair Languages);

    private readonly record struct LanguagePair(string Source, string Target);

    private readonly record struct SegmentPlan(
        QaSegmentInput Segment,
        double WindowSeconds,
        int MaxChars,
        string? HostedTranslation);
}

/// <summary>Inputs for one Spark Open translation-lane run.</summary>
public sealed record TranslationRunRequest(
    IReadOnlyList<QaSegmentInput> Segments,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyDictionary<string, string>? HostedTranslations = null);
