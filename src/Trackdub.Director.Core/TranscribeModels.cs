using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// One diarization turn exactly as the ASR endpoint returns it (probe-verified
/// shape: samples/voa10-scale/fpc-transcribe-raw.json). Stamps are milliseconds
/// relative to the submitted audio. Speaker is the verbatim diarization label;
/// the endpoint omits it on turns with no attributable speech (e.g. music), so
/// it is nullable and never invented.
/// </summary>
public sealed record TranscribeTurn(
    [property: JsonPropertyName("turnId")] int TurnId,
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("endMs")] long EndMs,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("speaker")] string? Speaker);

/// <summary>
/// One transcribed segment in the app's input shape ({id, text, duration_seconds})
/// plus the diarization speaker label, recorded verbatim ("A", "B", ...) and never
/// mapped to a character name. DurationSeconds is the turn's endMs minus startMs,
/// rounded to 0.1 s, matching the voa10 stamping convention. Nullable so pipeline
/// / web inputs can omit duration (QA then estimates); ASR mapping always sets it.
/// </summary>
public sealed record TranscribeSegment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds,
    [property: JsonPropertyName("speaker")] string? Speaker = null);

/// <summary>
/// Raw ASR response as parsed by the client, before segment mapping. The endpoint
/// reports no token usage (billed per audio hour), so there is no usage field here
/// at all: nothing to record, never a zero.
/// </summary>
public sealed record TranscribeRawResponse(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("audioDurationMs")] long AudioDurationMs,
    [property: JsonPropertyName("turns")] TranscribeTurn[] Turns);

/// <summary>
/// Transcribe stage result. Usage is always null: the ASR endpoint reports no
/// token usage (billed per audio hour), so usage is recorded as null / not
/// reported, never as zero — matching docs/metrics-snapshot.md's transcription
/// lane. Model is the requested model; the endpoint does not echo one.
/// </summary>
public sealed record TranscribeResult(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint_host")] string EndpointHost,
    [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("audio_duration_ms")] long AudioDurationMs,
    [property: JsonPropertyName("segments")] TranscribeSegment[] Segments,
    [property: JsonPropertyName("turns")] TranscribeTurn[] Turns,
    [property: JsonPropertyName("usage")] MetaClient.TokenUsage? Usage)
{
    /// <summary>Always false: the ASR endpoint reports no token usage. Serialized so a missing usage object reads as "not reported", not as a bug.</summary>
    [JsonPropertyName("usage_reported")]
    public bool UsageReported => false;
}
