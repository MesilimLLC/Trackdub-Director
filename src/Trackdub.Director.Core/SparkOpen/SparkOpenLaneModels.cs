using System.Text.Json.Serialization;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>Production-lane report: the existing QA report wrapped with the Spark Open provenance and a plain gate-verified flag.</summary>
public sealed record SparkOpenProductionReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("spark_open_model")] string SparkOpenModel,
    [property: JsonPropertyName("spark_open_endpoint_host")] string SparkOpenEndpointHost,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("fully_gate_verified")] bool FullyGateVerified,
    [property: JsonPropertyName("qa_report")] QaReport QaReport);

/// <summary>
/// One translation-lane line: Spark Open produced the translation; the existing gates graded it.
/// Timing is <see cref="DurationEstimator"/>; meaning is the hosted <see cref="MeaningGate"/>
/// (muse-spark-1.1, meaning-v1), so the open-model translation is judged by the same machinery the
/// hosted lane uses. <see cref="HostedTranslation"/> carries the hosted lane's recorded result on the
/// same source line for a side-by-side.
/// </summary>
public sealed record SparkOpenTranslationLineResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("spark_open_translation")] string SparkOpenTranslation,
    [property: JsonPropertyName("estimated_seconds")] double EstimatedSeconds,
    [property: JsonPropertyName("max_chars")] int MaxChars,
    [property: JsonPropertyName("timing_verdict")] QaVerdict TimingVerdict,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict MeaningVerdict,
    [property: JsonPropertyName("meaning_issues")] string[]? MeaningIssues,
    [property: JsonPropertyName("meaning_explanation")] string? MeaningExplanation,
    [property: JsonPropertyName("meaning_model_id")] string? MeaningModelId,
    [property: JsonPropertyName("combined_verdict")] QaVerdict CombinedVerdict,
    [property: JsonPropertyName("spark_open_usage")] MetaClient.TokenUsage? SparkOpenTranslationUsage,
    [property: JsonPropertyName("hosted_meaning_usage")] MetaClient.TokenUsage? HostedMeaningUsage,
    [property: JsonPropertyName("hosted_translation")] string? HostedTranslation = null,
    [property: JsonPropertyName("translation_error")] string? TranslationError = null);

/// <summary>
/// Pass rates for the Spark Open translation lane against the hosted lane's recorded rates on the
/// same set. Timing pass is DurationEstimator fit; meaning pass is the hosted gate; combined pass is
/// both. A meaning Error counts as a meaning fail for the combined rate but is tallied separately.
/// </summary>
public sealed record SparkOpenTranslationSummary(
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("timing_pass")] int TimingPass,
    [property: JsonPropertyName("meaning_pass")] int MeaningPass,
    [property: JsonPropertyName("combined_pass")] int CombinedPass,
    [property: JsonPropertyName("meaning_errors")] int MeaningErrors,
    [property: JsonPropertyName("translation_errors")] int TranslationErrors,
    [property: JsonPropertyName("timing_pass_rate")] double? TimingPassRate,
    [property: JsonPropertyName("meaning_pass_rate")] double? MeaningPassRate,
    [property: JsonPropertyName("combined_pass_rate")] double? CombinedPassRate,
    [property: JsonPropertyName("spark_open_translation_tokens")] int SparkOpenTranslationTokens,
    [property: JsonPropertyName("hosted_meaning_tokens")] int HostedMeaningTokens);

public sealed record SparkOpenTranslationReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("translation_provider")] string TranslationProvider,
    [property: JsonPropertyName("meaning_provider")] string MeaningProvider,
    [property: JsonPropertyName("spark_open_model")] string SparkOpenModel,
    [property: JsonPropertyName("spark_open_endpoint_host")] string SparkOpenEndpointHost,
    [property: JsonPropertyName("meaning_criteria_version")] string MeaningCriteriaVersion,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("target_cps")] double TargetCharactersPerSecond,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("summary")] SparkOpenTranslationSummary Summary,
    [property: JsonPropertyName("results")] SparkOpenTranslationLineResult[] Results);
