using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Hash-sealed verify-only receipt over a captured run. Never described as
/// signed, certified, or attested.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyOnlySegmentVerdict
{
    Verified,
    HumanReviewRequired,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyOnlyReason
{
    Uncast,
    TimingFail,
    MeaningFail,
    MeaningNotEvaluated,
    MediaNotPresent,
    MeasuredDurationMissing,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyOnlyHashProvenance
{
    [JsonStringEnumMemberName("recomputed")]
    Recomputed,

    [JsonStringEnumMemberName("recorded-at-capture")]
    RecordedAtCapture,
}

public sealed record VerifyOnlyHashEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("provenance")] VerifyOnlyHashProvenance Provenance);

public sealed record VerifyOnlyRefusal(
    [property: JsonPropertyName("take_number")] int TakeNumber,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reasons")] string[] Reasons,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict MeaningVerdict,
    [property: JsonPropertyName("meaning_explanation")] string? MeaningExplanation,
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("timing_recomputed_pass")] bool? TimingRecomputedPass);

public sealed record VerifyOnlySegmentReceipt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("spanish_text")] string? SpanishText,
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("timing_recomputed_pass")] bool? TimingRecomputedPass,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict? MeaningVerdict,
    [property: JsonPropertyName("meaning_explanation")] string? MeaningExplanation,
    [property: JsonPropertyName("winning_voice")] AuditionCastVoice? WinningVoice,
    [property: JsonPropertyName("winner_file_name")] string? WinnerFileName,
    [property: JsonPropertyName("winning_media_hash")] VerifyOnlyHashEntry? WinningMediaHash,
    [property: JsonPropertyName("media_present")] bool MediaPresent,
    [property: JsonPropertyName("refusals")] VerifyOnlyRefusal[] Refusals,
    [property: JsonPropertyName("verdict")] VerifyOnlySegmentVerdict Verdict,
    [property: JsonPropertyName("reasons")] VerifyOnlyReason[] Reasons);

public sealed record VerifyOnlyReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("run_dir")] string RunDir,
    [property: JsonPropertyName("overall_verdict")] VerifyOnlySegmentVerdict OverallVerdict,
    [property: JsonPropertyName("exit_status")] int ExitStatus,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("input_hashes")] VerifyOnlyHashEntry[] InputHashes,
    [property: JsonPropertyName("segments")] VerifyOnlySegmentReceipt[] Segments,
    [property: JsonPropertyName("notes")] string Notes);

public sealed record VerifyOnlyResult(VerifyOnlyReceipt Receipt, int ExitCode);
