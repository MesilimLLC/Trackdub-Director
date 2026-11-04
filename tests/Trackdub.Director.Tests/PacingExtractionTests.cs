using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class PacingExtractionTests
{
    [Fact]
    public void Vision_parser_carries_pacing_label_when_present()
    {
        const string json = """{"scene_summary":"A cafe.","emotion_tone_label":"cheerful","pacing_label":"unhurried, with pauses","speaker_description":"A woman.","on_screen_text":[],"burned_in_text":[]}""";

        VisionParseResult result = SceneVisionParser.Parse(json);

        Assert.False(result.IsMalformed);
        Assert.Equal("unhurried, with pauses", result.Enrichment!.PacingLabel);
        Assert.Equal("cheerful", result.Enrichment.EmotionToneLabel);
    }

    [Fact]
    public void Vision_parser_accepts_legacy_response_without_pacing_label()
    {
        const string json = """{"scene_summary":"A cafe.","emotion_tone_label":"cheerful","speaker_description":"A woman.","on_screen_text":[],"burned_in_text":[]}""";

        VisionParseResult result = SceneVisionParser.Parse(json);

        Assert.False(result.IsMalformed);
        Assert.Null(result.Enrichment!.PacingLabel);
    }

    [Theory]
    [InlineData("brisk", "[briskly]")]
    [InlineData("halting with pauses", "[pauses]")]
    [InlineData("halting", "[hesitantly]")]
    [InlineData("unhurried", "[slowly]")]
    public void Pacing_keywords_map_to_pacing_tags(string label, string expected)
    {
        Assert.Equal(expected, EmotionTagMap.ForLabel(label));
    }

    [Fact]
    public void Emotion_and_pacing_labels_combine_into_one_tag_string()
    {
        string combined = string.Join(", ", new[] { "cheerful", "unhurried, with pauses" });
        Assert.Equal("[cheerfully] [slowly]", EmotionTagMap.ForLabel(combined));
    }

    [Fact]
    public void Vision_prompt_asks_for_pacing()
    {
        Assert.Contains("pacing_label", SceneContextService.VisionPromptText, StringComparison.Ordinal);
        Assert.Equal("scene-vision-v2", SceneContextService.VisionPromptTemplateVersion);
    }
}
