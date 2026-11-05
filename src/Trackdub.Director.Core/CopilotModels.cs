using System.Text.Json;
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

// Presence is tracked separately from value for the fields the browser uses to
// clear state (director register and sign text). The global serializer omits
// nulls, so an explicit null proposed by the model would otherwise look like an
// omitted field and the browser would not clear the existing value.
[JsonConverter(typeof(CopilotSegmentUpdateConverter))]
public sealed record CopilotSegmentUpdate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("concepts")] string[]? Concepts = null,
    [property: JsonPropertyName("vision_enrichment")] bool? VisionEnrichment = null,
    [property: JsonPropertyName("director_register")] string? DirectorRegister = null,
    [property: JsonPropertyName("sign_source_text")] string? SignSourceText = null,
    [property: JsonPropertyName("sign_target_text")] string? SignTargetText = null)
{
    [JsonIgnore] public bool DirectorRegisterSpecified { get; init; }
    [JsonIgnore] public bool SignSourceTextSpecified { get; init; }
    [JsonIgnore] public bool SignTargetTextSpecified { get; init; }
}

public sealed class CopilotSegmentUpdateConverter : JsonConverter<CopilotSegmentUpdate>
{
    public override CopilotSegmentUpdate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Copilot segment updates are produced by the server, not read from JSON.");

    public override void Write(Utf8JsonWriter writer, CopilotSegmentUpdate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (value.Concepts is not null)
        {
            writer.WritePropertyName("concepts");
            JsonSerializer.Serialize(writer, value.Concepts, options);
        }

        if (value.VisionEnrichment is not null)
        {
            writer.WriteBoolean("vision_enrichment", value.VisionEnrichment.Value);
        }

        WriteOptional(writer, "director_register", value.DirectorRegister, value.DirectorRegisterSpecified);
        WriteOptional(writer, "sign_source_text", value.SignSourceText, value.SignSourceTextSpecified);
        WriteOptional(writer, "sign_target_text", value.SignTargetText, value.SignTargetTextSpecified);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value, bool specified)
    {
        if (!specified) return;
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}

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
