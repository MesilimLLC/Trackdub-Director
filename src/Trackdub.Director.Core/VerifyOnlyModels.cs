using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SegmentVerdict
{
    Verified,
    HumanReviewRequired,
}

/// <summary>Machine-readable reasons a segment needs a human. NotEvaluated is a review reason, never a pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyReason
{
    Uncast,
    TimingFail,
    MeaningFail,
    MeaningNotEvaluated,
    MediaNotPresent,

    // Only possible when the manifest recorded the take's hash: the file on disk is not the file that was captured.
    MediaHashMismatch,
}

/// <summary>How a hash in a receipt was obtained. A recorded hash is never labelled as recomputed.</summary>
public static class HashLabels
{
    public const string Recomputed = "recomputed";
    public const string RecordedAtCapture = "recorded-at-capture";
    public const string RecordedInSidecar = "recorded-in-sidecar";
    public const string NotRecorded = "not-recorded-in-manifest";
}

public sealed record InputHash(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("label")] string Label);

public sealed record TimingCheck(
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("allowed_seconds")] double AllowedSeconds,
    [property: JsonPropertyName("verdict")] AuditionTimingVerdict Verdict);

public sealed record MeaningCheck(
    [property: JsonPropertyName("verdict")] MeaningVerdict Verdict,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("issues")] string[]? Issues);

public sealed record MediaCheck(
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("hash_label")] string HashLabel);

public sealed record RefusedTake(
    [property: JsonPropertyName("take_number")] int TakeNumber,
    [property: JsonPropertyName("kind")] QaAttemptKind Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict MeaningVerdict,
    [property: JsonPropertyName("meaning_reason")] string? MeaningReason,
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("reasons")] VerifyReason[] Reasons,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record SegmentReceipt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_line")] string SourceLine,
    [property: JsonPropertyName("spanish")] string? Spanish,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("winning_voice")] AuditionCastVoice? WinningVoice,
    [property: JsonPropertyName("winning_take_number")] int? WinningTakeNumber,
    [property: JsonPropertyName("timing")] TimingCheck? Timing,
    [property: JsonPropertyName("meaning")] MeaningCheck? Meaning,
    [property: JsonPropertyName("media")] MediaCheck? Media,
    [property: JsonPropertyName("refusals")] RefusedTake[] Refusals,
    [property: JsonPropertyName("verdict")] SegmentVerdict Verdict,
    [property: JsonPropertyName("reasons")] VerifyReason[] Reasons);

public sealed record VerifyReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("seal")] string Seal,
    [property: JsonPropertyName("run_folder")] string RunFolder,
    [property: JsonPropertyName("media_folder")] string MediaFolder,
    [property: JsonPropertyName("tolerance_fraction")] double ToleranceFraction,
    [property: JsonPropertyName("verdict")] SegmentVerdict Verdict,
    [property: JsonPropertyName("exit_status")] int ExitStatus,
    [property: JsonPropertyName("segments_total")] int SegmentsTotal,
    [property: JsonPropertyName("segments_verified")] int SegmentsVerified,
    [property: JsonPropertyName("voices")] AuditionCastVoice[] Voices,
    [property: JsonPropertyName("inputs")] InputHash[] Inputs,
    [property: JsonPropertyName("segments")] SegmentReceipt[] Segments)
{
    public const string SchemaId = "trackdub-director.verify-only/v1";

    public const string SealStatement =
        "Hash-sealed: every consumed input artifact and every present winning media file is listed with its SHA-256 and how that hash was obtained. Verdicts are recomputed from recorded numbers; no summary field is trusted.";

    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, ReceiptJson);

    public static VerifyReceipt FromJson(string json)
    {
        return JsonSerializer.Deserialize<VerifyReceipt>(json, ReceiptJson)
            ?? throw new JsonException("Receipt JSON is empty.");
    }
}

/// <summary>Input that cannot be read as a captured run (CLI exit 2, API 400). Never used for a failed verdict.</summary>
public sealed class VerifyInputException(string message) : Exception(message);
