using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Configuration for carrying detected delivery (emotion, pacing, pauses)
/// into TTS generation as provider audio tags. Disabled by default; enabling
/// is per-run configuration. Tags are only ever sent to models that honor
/// them as control (see <see cref="ITtsProvider.SupportsEmotionTags"/>):
/// measured 2026-11-02, the v2 model speaks bracket tags aloud, v3 honors
/// them, and v4 is probed for the same behavior before it is trusted.
/// </summary>
public sealed record EmotionTagOptions(
    [property: JsonPropertyName("enabled")] bool Enabled = true)
{
    public static readonly EmotionTagOptions Disabled = new(false);

    public static readonly EmotionTagOptions EnabledDefault = new(true);
}

/// <summary>
/// Maps a free-text detected delivery label (emotion, pacing, pauses, as
/// extracted by the scene/Director Muse passes) to provider audio tags.
/// The mapped vocabulary is deliberately wider than the probed set, but the
/// rule is unchanged: a label with no mapped keyword gets no tag, never an
/// invented one. Emotion and pacing keywords can combine (at most one of
/// each) so a label like "warm, unhurried" yields "[warmly] [slowly]".
/// </summary>
public static class EmotionTagMap
{
    private static readonly (string Keyword, string Tag)[] EmotionMap =
    [
        ("cheerful", "[cheerfully]"),
        ("joyful", "[joyfully]"),
        ("happy", "[happily]"),
        ("excited", "[excitedly]"),
        ("enthusiastic", "[enthusiastically]"),
        ("playful", "[playfully]"),
        ("amused", "[amusedly]"),
        ("proud", "[proudly]"),
        ("relieved", "[relieved]"),
        ("hopeful", "[hopefully]"),
        ("grateful", "[gratefully]"),
        ("affectionate", "[affectionately]"),
        ("warm", "[warmly]"),
        ("friendly", "[warmly]"),
        ("tender", "[tenderly]"),
        ("romantic", "[romantically]"),
        ("curious", "[curiously]"),
        ("interested", "[interestedly]"),
        ("surprised", "[surprised]"),
        ("surprise", "[surprised]"),
        ("astonished", "[astonished]"),
        ("shocked", "[shocked]"),
        ("sad", "[sadly]"),
        ("melancholy", "[melancholically]"),
        ("lonely", "[lonelily]"),
        ("disappointed", "[disappointedly]"),
        ("angry", "[angrily]"),
        ("frustrated", "[frustratedly]"),
        ("annoyed", "[annoyedly]"),
        ("anxious", "[anxiously]"),
        ("nervous", "[nervously]"),
        ("worried", "[worriedly]"),
        ("afraid", "[fearfully]"),
        ("fearful", "[fearfully]"),
        ("embarrassed", "[embarrassed]"),
        ("sheepish", "[sheepishly]"),
        ("ashamed", "[ashamedly]"),
        ("guilty", "[guiltily]"),
        ("shy", "[shyly]"),
        ("confused", "[confusedly]"),
        ("uncertain", "[uncertainly]"),
        ("hesitant", "[hesitantly]"),
        ("calm", "[calmly]"),
        ("relaxed", "[relaxedly]"),
        ("serious", "[seriously]"),
        ("determined", "[determinedly]"),
        ("confident", "[confidently]"),
        ("tired", "[tiredly]"),
        ("bored", "[boredly]"),
        ("sarcastic", "[sarcastically]"),
        ("wry", "[wryly]"),
        ("deadpan", "[deadpan]"),
        ("solemn", "[solemnly]"),
        ("urgent", "[urgently]"),
    ];

    private static readonly (string Keyword, string Tag)[] PacingMap =
    [
        ("unhurried", "[slowly]"),
        ("slow", "[slowly]"),
        ("deliberate", "[deliberately]"),
        ("measured pace", "[deliberately]"),
        ("rushed", "[rushed]"),
        ("hurried", "[rushed]"),
        ("fast-paced", "[rushed]"),
        ("brisk", "[briskly]"),
        ("pause", "[pauses]"),
        ("pauses", "[pauses]"),
        ("halting", "[hesitantly]"),
    ];

    /// <summary>
    /// The tags for a label: at most one emotion tag plus at most one pacing
    /// tag, in that order. Empty when the label names nothing mappable
    /// (including neutral): no tag is ever invented for an unrecognized label.
    /// </summary>
    public static IReadOnlyList<string> TagsForLabel(string? emotionToneLabel)
    {
        if (string.IsNullOrWhiteSpace(emotionToneLabel))
        {
            return [];
        }

        string normalized = emotionToneLabel.Trim().ToLowerInvariant();
        if (normalized.Contains("neutral", StringComparison.Ordinal))
        {
            return [];
        }

        string? emotion = FirstMatch(normalized, EmotionMap);
        string? pacing = FirstMatch(normalized, PacingMap);
        return new[] { emotion, pacing }.Where(t => t is not null).Cast<string>().ToArray();
    }

    /// <summary>The single joined tag string for a label, or null when unmappable.</summary>
    public static string? ForLabel(string? emotionToneLabel)
    {
        IReadOnlyList<string> tags = TagsForLabel(emotionToneLabel);
        return tags.Count == 0 ? null : string.Join(" ", tags);
    }

    private static string? FirstMatch(string normalized, (string Keyword, string Tag)[] map)
    {
        foreach ((string keyword, string tag) in map)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }
}
