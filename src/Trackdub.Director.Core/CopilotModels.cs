using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

public sealed record CopilotSegmentSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds = null,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId = null,
    [property: JsonPropertyName("has_frame")] bool HasFrame = false,
    [property: JsonPropertyName("concepts")] string[]? Concepts = null,
    [property: JsonPropertyName("vision_enrichment")] bool VisionEnrichment = false,
    [property: JsonPropertyName("director_register")] string? DirectorRegister = null,
    [property: JsonPropertyName("has_sign_box")] bool HasSignBox = false,
    [property: JsonPropertyName("sign_source_text")] string? SignSourceText = null,
    [property: JsonPropertyName("sign_target_text")] string? SignTargetText = null);

public sealed record CopilotSettingsProposal(
    [property: JsonPropertyName("max_revisions")] int? MaxRevisions = null,
    [property: JsonPropertyName("tolerance_percent")] double? TolerancePercent = null,
    [property: JsonPropertyName("target_cps")] double? TargetCps = null,
    [property: JsonPropertyName("speed_retake_enabled")] bool? SpeedRetakeEnabled = null,
    [property: JsonPropertyName("speed_cap")] double? SpeedCap = null,
    [property: JsonPropertyName("emotion_tags_enabled")] bool? EmotionTagsEnabled = null);

public sealed record CopilotRequest(
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("settings")] CopilotSettingsProposal Settings,
    [property: JsonPropertyName("segments")] CopilotSegmentSummary[] Segments,
    [property: JsonPropertyName("report_summary")] string? ReportSummary = null);

public sealed record CopilotSegmentUpdate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("concepts")] string[]? Concepts = null,
    [property: JsonPropertyName("vision_enrichment")] bool? VisionEnrichment = null,
    [property: JsonPropertyName("director_register")] string? DirectorRegister = null,
    [property: JsonPropertyName("sign_source_text")] string? SignSourceText = null,
    [property: JsonPropertyName("sign_target_text")] string? SignTargetText = null);

public sealed record CopilotProposal(
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("settings")] CopilotSettingsProposal Settings,
    [property: JsonPropertyName("segment_updates")] CopilotSegmentUpdate[] SegmentUpdates,
    [property: JsonPropertyName("missing_inputs")] string[] MissingInputs,
    [property: JsonPropertyName("warnings")] string[] Warnings,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] MetaClient.TokenUsage? Usage,
    [property: JsonPropertyName("prompt_template_version")] string PromptTemplateVersion);
