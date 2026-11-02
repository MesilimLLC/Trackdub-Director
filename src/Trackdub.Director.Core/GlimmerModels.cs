using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// The action the Glimmer recovery agent returns for one attempt. Glimmer PROPOSES;
/// the deterministic gates DISPOSE. Exactly one action per reply:
/// <see cref="Propose"/> carries a candidate line to judge; <see cref="GiveUp"/> ends the item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlimmerActionKind
{
    Propose,
    GiveUp,
}

/// <summary>
/// One schema-constrained Glimmer reply. The wire shape is
/// {"action":"propose","text_es":"...","strategy":"..."} or
/// {"action":"give_up","strategy":"..."}. A reply that does not match this shape
/// is a malformed reply and must be surfaced as such (never repaired), so the
/// harness can record it as a failed attempt rather than a proposal.
/// </summary>
public sealed record GlimmerAction(
    [property: JsonPropertyName("action")] GlimmerActionKind Action,
    [property: JsonPropertyName("text_es")] string? TextEs,
    [property: JsonPropertyName("strategy")] string Strategy);

/// <summary>
/// One Glimmer reply plus the usage the route billed for it. <see cref="Malformed"/>
/// is true when the raw reply could not be parsed into a single on-schema action;
/// <see cref="Action"/> is then null and the attempt counts as a failed attempt.
/// <see cref="RawContent"/> preserves the model text for the trace.
/// </summary>
public sealed record GlimmerReply(
    GlimmerAction? Action,
    bool Malformed,
    string RawContent,
    string Model,
    MetaClient.TokenUsage? Usage)
{
    public static GlimmerReply ForMalformed(string rawContent, string model, MetaClient.TokenUsage? usage)
        => new(null, true, rawContent, model, usage);

    public static GlimmerReply ForAction(GlimmerAction action, string rawContent, string model, MetaClient.TokenUsage? usage)
        => new(action, false, rawContent, model, usage);
}

/// <summary>
/// Result of the single pre-flight probe call. Its existence means the probe passed (the client
/// throws on any failure, including a reply that is not {"ok": true} or a non-Glimmer returned
/// model). Carries only what the report needs: the endpoint <see cref="Host"/> (never the key),
/// the <see cref="RequestedModel"/> sent and the <see cref="ReturnedModel"/> the route reported,
/// the billed <see cref="Usage"/>, and the probe's <see cref="WallSeconds"/>.
/// </summary>
public sealed record GlimmerProbeResult(
    string Host,
    string RequestedModel,
    string ReturnedModel,
    MetaClient.TokenUsage? Usage,
    double WallSeconds);

/// <summary>
/// One recovery item: a single refused candidate that the existing Spark loop had
/// already given up on, carried into the agent with ONLY the leakage-safe input.
/// The agent never sees any later revision, retry, or accepted candidate for the
/// same segment, nor any seeded meaning-eval label.
/// </summary>
public sealed record RecoveryItem(
    [property: JsonPropertyName("source_file")] string SourceFile,
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("source_line")] string SourceLine,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("target_cps")] double TargetCharactersPerSecond,
    [property: JsonPropertyName("failure_type")] RecoveryFailureType FailureType,
    [property: JsonPropertyName("refused_candidate")] string RefusedCandidate,
    [property: JsonPropertyName("gate_failure_reasons")] string[] GateFailureReasons,
    [property: JsonPropertyName("scene_context")] string? SceneContext = null);

/// <summary>Why the existing loop refused the failed attempt this item is built from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryFailureType
{
    /// <summary>The attempt overran its timing window.</summary>
    Timing,

    /// <summary>The attempt fit the window but the meaning gate failed it.</summary>
    Meaning,
}

/// <summary>
/// One recorded recovery attempt inside an item's trace. Every attempt is recorded,
/// including malformed Glimmer replies (which never count as a proposal) and give_up.
/// </summary>
public sealed record RecoveryAttemptTrace(
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("glimmer_action")] GlimmerActionKind? GlimmerAction,
    [property: JsonPropertyName("malformed_reply")] bool MalformedReply,
    [property: JsonPropertyName("proposed_text")] string? ProposedText,
    [property: JsonPropertyName("strategy")] string? Strategy,
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("timing_verdict")] QaVerdict TimingVerdict,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict MeaningVerdict,
    [property: JsonPropertyName("meaning_issues")] string[]? MeaningIssues,
    [property: JsonPropertyName("meaning_explanation")] string? MeaningExplanation,
    [property: JsonPropertyName("attempt_recovered")] bool AttemptRecovered,
    [property: JsonPropertyName("glimmer_usage")] GlimmerUsageRecord GlimmerUsage,
    [property: JsonPropertyName("meaning_usage")] GlimmerUsageRecord MeaningUsage);

/// <summary>Flat token usage for one provider call in the trace. Zero when the provider was not called.</summary>
public sealed record GlimmerUsageRecord(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens)
{
    public static readonly GlimmerUsageRecord Zero = new(0, 0, 0);

    public static GlimmerUsageRecord From(MetaClient.TokenUsage? usage)
        => usage is null ? Zero : new GlimmerUsageRecord(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
}

/// <summary>The final disposition of one recovery item after the loop stops.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryOutcome
{
    /// <summary>A Glimmer proposal passed BOTH gates. The line is recovered.</summary>
    Recovered,

    /// <summary>The attempt cap was reached with no passing proposal.</summary>
    ExhaustedAttempts,

    /// <summary>Glimmer chose give_up before the cap.</summary>
    GaveUp,

    /// <summary>The run's provider budget was exhausted before the item could finish.</summary>
    BudgetExhausted,
}

/// <summary>One item's recovery result: its leakage-safe input, every attempt, and the outcome.</summary>
public sealed record RecoveryItemReport(
    [property: JsonPropertyName("source_file")] string SourceFile,
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("source_line")] string SourceLine,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("failure_type")] RecoveryFailureType FailureType,
    [property: JsonPropertyName("outcome")] RecoveryOutcome Outcome,
    [property: JsonPropertyName("recovered_text")] string? RecoveredText,
    [property: JsonPropertyName("attempts")] RecoveryAttemptTrace[] Attempts);

/// <summary>
/// Run-level totals for the recovery report, reported BOTH ways so the recovery rate
/// cannot be read as double-counting:
/// - the per-ATTEMPT population (<see cref="TotalItems"/> etc.): one count per refused
///   attempt, which is the item population — a single source line refused on two
///   attempts contributes two items;
/// - the per-distinct-LINE rollup (<see cref="DistinctLines"/> etc.): one count per
///   distinct (source_file, segment_id), where a line counts as recovered if ANY of its
///   items recovered. This is the honest "lines recovered out of lines refused" rate.
/// For the recorded set these differ (4 attempts across 3 distinct lines: seg-006 was
/// refused on two attempts), so both are surfaced explicitly.
/// </summary>
public sealed record RecoveryTotals(
    [property: JsonPropertyName("total_items")] int TotalItems,
    [property: JsonPropertyName("recovered")] int Recovered,
    [property: JsonPropertyName("still_refused")] int StillRefused,
    [property: JsonPropertyName("distinct_lines")] int DistinctLines,
    [property: JsonPropertyName("distinct_lines_recovered")] int DistinctLinesRecovered,
    [property: JsonPropertyName("distinct_lines_still_refused")] int DistinctLinesStillRefused,
    [property: JsonPropertyName("glimmer_calls")] int GlimmerCalls,
    [property: JsonPropertyName("meta_calls")] int MetaCalls,
    [property: JsonPropertyName("glimmer_call_cap")] int GlimmerCallCap,
    [property: JsonPropertyName("meta_call_cap")] int MetaCallCap,
    [property: JsonPropertyName("glimmer_tokens")] GlimmerUsageRecord GlimmerTokens,
    [property: JsonPropertyName("meta_tokens")] GlimmerUsageRecord MetaTokens);

/// <summary>Probe details persisted in the run report: endpoint host (never the key), the
/// requested and served models, billed tokens, and wall seconds.</summary>
public sealed record RecoveryProbeInfo(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("requested_model")] string RequestedModel,
    [property: JsonPropertyName("returned_model")] string ReturnedModel,
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("wall_seconds")] double WallSeconds);

/// <summary>The full recovery run report written to samples/glimmer-recovery/recovery-report.json (Phase 2).</summary>
public sealed record RecoveryReport(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("glimmer_model")] string GlimmerModel,
    [property: JsonPropertyName("glimmer_reasoning_effort")] string? GlimmerReasoningEffort,
    [property: JsonPropertyName("meaning_model")] string MeaningModel,
    [property: JsonPropertyName("meaning_prompt_template_version")] string MeaningPromptTemplateVersion,
    [property: JsonPropertyName("max_attempts_per_item")] int MaxAttemptsPerItem,
    [property: JsonPropertyName("probe")] RecoveryProbeInfo? Probe,
    [property: JsonPropertyName("run_error")] string? RunError,
    [property: JsonPropertyName("items")] RecoveryItemReport[] Items,
    [property: JsonPropertyName("totals")] RecoveryTotals Totals);
