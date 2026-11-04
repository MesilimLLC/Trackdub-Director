using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Configuration for carrying detected delivery (emotion, pacing, pauses)
/// into TTS generation as provider audio tags. Enabled by default; can be
/// disabled per run via EmotionTagOptions.Disabled. Tags are only ever sent to models that honor
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

public readonly record struct EmotionLabel
{
    internal string? Normalized { get; init; }

    public bool IsEmpty => string.IsNullOrEmpty(Normalized);

    public static implicit operator EmotionLabel(string? value)
        => new() { Normalized = value?.Trim().ToLowerInvariant() };

    internal string? FirstMatch((string Keyword, string Tag)[] map)
        => new LabelScanner(this, map).FirstMatch();

    private sealed class LabelScanner(EmotionLabel label, (string Keyword, string Tag)[] map)
    {
        private static readonly string[] Negators = ["not", "no", "never", "without"];

        private readonly string _text = label.Normalized ?? string.Empty;
        private readonly (string Keyword, string Tag)[] _map = map;

        public string? FirstMatch()
        {
            foreach (var entry in _map)
            {
                if (HasUnnegatedWholeWord(entry))
                {
                    return entry.Tag;
                }
            }

            return null;
        }

        private bool HasUnnegatedWholeWord((string Keyword, string Tag) entry)
        {
            int index = _text.IndexOf(entry.Keyword, StringComparison.Ordinal);
            while (index >= 0)
            {
                var occurrence = new WordOccurrence { Start = index, Length = entry.Keyword.Length };
                if (IsWholeWordAt(occurrence) && !IsNegatedAt(occurrence))
                {
                    return true;
                }

                index = _text.IndexOf(entry.Keyword, index + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private bool IsWholeWordAt(WordOccurrence occurrence)
        {
            bool startOk = occurrence.Start == 0
                || !(char.IsLetterOrDigit(_text[occurrence.Start - 1]) || _text[occurrence.Start - 1] is '-' or '_');
            int end = occurrence.Start + occurrence.Length;
            bool endOk = end == _text.Length
                || !(char.IsLetterOrDigit(_text[end]) || _text[end] is '-' or '_');
            return startOk && endOk;
        }

        private bool IsNegatedAt(WordOccurrence occurrence)
        {
            int wordEnd = occurrence.Start;
            while (wordEnd > 0 && _text[wordEnd - 1] == ' ')
            {
                wordEnd--;
            }

            int wordStart = wordEnd;
            while (wordStart > 0 && _text[wordStart - 1] != ' ')
            {
                wordStart--;
            }

            string preceding = _text[wordStart..wordEnd];
            return Negators.Contains(preceding, StringComparer.Ordinal);
        }

        private sealed class WordOccurrence
        {
            public int Start { get; init; }

            public int Length { get; init; }
        }
    }
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
    /// tag, in that order. Empty when the label names nothing mappable: no
    /// tag is ever invented for an unrecognized label. A keyword counts only
    /// as a whole word and only when a negator does not govern it, so
    /// "unhappy" or "not happy" never maps to "[happily]" and a neutral
    /// emotion label still lets a pacing label through (e.g. "neutral, brisk").
    /// </summary>
    public static IReadOnlyList<string> TagsForLabel(EmotionLabel label)
    {
        if (label.IsEmpty)
        {
            return [];
        }

        string? emotion = label.FirstMatch(EmotionMap);
        string? pacing = label.FirstMatch(PacingMap);
        return new[] { emotion, pacing }.Where(t => t is not null).Cast<string>().ToArray();
    }

    /// <summary>The single joined tag string for a label, or null when unmappable.</summary>
    public static string? ForLabel(EmotionLabel label)
    {
        IReadOnlyList<string> tags = TagsForLabel(label);
        return tags.Count == 0 ? null : string.Join(" ", tags);
    }
}
