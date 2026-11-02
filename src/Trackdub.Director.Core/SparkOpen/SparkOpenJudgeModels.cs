using System.Text.Json.Serialization;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// One item for the independent-judge comparison: a source line and a Spanish candidate the hosted
/// model already judged, with the hosted verdict recorded for diffing. <see cref="Source"/> names
/// where the item came from (cases.json, the pressure run, or the uniform run) for enumeration.
/// </summary>
public sealed record JudgeItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("hosted_verdict")] MeaningVerdict HostedVerdict,
    [property: JsonPropertyName("hosted_reason")] string? HostedReason = null);

/// <summary>
/// How one item's two verdicts relate. <see cref="Excluded"/> covers any item Spark Open could not
/// judge (Error/unparseable) or whose hosted verdict is not a clean Pass/Fail — it counts toward the
/// excluded tally and is left out of agreement entirely, never scored as agreement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JudgeComparison
{
    Agree,
    Disagree,
    Excluded,
}

public sealed record JudgeItemResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("hosted_verdict")] MeaningVerdict HostedVerdict,
    [property: JsonPropertyName("hosted_reason")] string? HostedReason,
    [property: JsonPropertyName("spark_open_verdict")] MeaningVerdict SparkOpenVerdict,
    [property: JsonPropertyName("spark_open_reason")] string SparkOpenReason,
    [property: JsonPropertyName("comparison")] JudgeComparison Comparison,
    [property: JsonPropertyName("spark_open_usage")] MetaClient.TokenUsage? SparkOpenUsage);

/// <summary>
/// Agreement computed over items BOTH models judged. <see cref="Compared"/> is the denominator:
/// items with a clean hosted Pass/Fail and a clean Spark Open Pass/Fail. <see cref="Excluded"/> items
/// (Spark Open Error/unparseable, or a non-clean hosted verdict) are counted but never scored.
/// <see cref="AgreementRate"/> is agree / compared, null when nothing was comparable.
/// </summary>
public sealed record JudgeAgreementSummary(
    [property: JsonPropertyName("total_items")] int TotalItems,
    [property: JsonPropertyName("compared")] int Compared,
    [property: JsonPropertyName("agree")] int Agree,
    [property: JsonPropertyName("disagree")] int Disagree,
    [property: JsonPropertyName("excluded")] int Excluded,
    [property: JsonPropertyName("agreement_rate")] double? AgreementRate,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record JudgeAgreementReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("spark_open_model")] string SparkOpenModel,
    [property: JsonPropertyName("spark_open_endpoint_host")] string SparkOpenEndpointHost,
    [property: JsonPropertyName("criteria_version")] string CriteriaVersion,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("summary")] JudgeAgreementSummary Summary,
    [property: JsonPropertyName("results")] JudgeItemResult[] Results);
