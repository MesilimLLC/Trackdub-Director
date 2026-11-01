using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Per-stage outcome in a pipeline run. Ran: the lane executed and its report is
/// referenced by the pipeline report. SkippedByFlag: the caller's flag (or a
/// missing prerequisite the flag implies, such as ELEVENLABS_API_KEY for the
/// audition stage) kept the lane from running — recorded as skipped, never as a
/// pass or a failure. Failed: the lane threw; downstream stages were never run.
/// NotRun: never attempted (no input for the stage, or an earlier stage failed).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineStageStatus
{
    Ran,
    SkippedByFlag,
    Failed,
    NotRun,
}

public sealed record PipelineStageReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] PipelineStageStatus Status,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Pipeline input. When AudioWav is present, stage 1 transcribes it and the
/// resulting segments drive the later lanes; otherwise Segments must be
/// non-empty. SceneSegments supply frames/concepts per segment id for the scene
/// lane; only entries whose id matches a working segment and that carry a frame
/// are sent. Audition is opt-in (default off); scene defaults on.
/// </summary>
public sealed record PipelineRequest(
    byte[]? AudioWav,
    IReadOnlyList<TranscribeSegment>? Segments,
    IReadOnlyList<SceneSegmentInput>? SceneSegments = null,
    bool Scene = true,
    bool Audition = false,
    string SourceLanguage = "en",
    string TargetLanguage = "es",
    string? AudioFileName = null);

/// <summary>
/// One orchestrated run. Each lane's report keeps its existing shape and
/// verdicts; the pipeline only references them and adds per-stage status.
/// Director adaptation is not a pipeline stage (per-line opt-in by design).
/// </summary>
public sealed record PipelineReport(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("stages")] PipelineStageReport[] Stages,
    [property: JsonPropertyName("segments")] TranscribeSegment[] Segments,
    [property: JsonPropertyName("transcribe")] TranscribeResult? Transcribe,
    [property: JsonPropertyName("qa")] QaReport? Qa,
    [property: JsonPropertyName("scene")] SceneContextBatch? Scene,
    [property: JsonPropertyName("audition")] AuditionReport? Audition);
