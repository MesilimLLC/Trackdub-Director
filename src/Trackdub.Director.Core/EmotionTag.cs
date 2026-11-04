using System.Text;
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

/// <summary>
/// A detected delivery label, normalized only mechanically for safe use as a
/// provider audio tag: brackets and control characters are removed and
/// whitespace is collapsed. The label's own wording is otherwise preserved.
/// </summary>
public readonly record struct EmotionLabel
{
    // A delivery label is a short phrase by contract ("short label" in every
    // extraction prompt). Anything longer is prose, not a label, and gets no
    // tag rather than a truncated one.
    private const int MaxLength = 120;

    private const int MaxWords = 16;

    internal string? Sanitized { get; init; }

    public bool IsEmpty => string.IsNullOrEmpty(Sanitized);

    public static implicit operator EmotionLabel(string? value)
        => new() { Sanitized = Sanitize(value) };

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char c in value.Trim())
        {
            if (c is '[' or ']' || char.IsControl(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        string sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0 || sanitized.Length > MaxLength)
        {
            return null;
        }

        return sanitized.Split(' ').Length > MaxWords ? null : sanitized;
    }
}

/// <summary>
/// Passes a detected delivery label (emotion, pacing, pauses, as extracted by
/// the scene/Director Muse passes) through to the TTS provider as one
/// bracketed audio tag, in the label's own words. Eleven v4 accepts grouped
/// natural-language traits in a single bracket (e.g. "[whispering, playful]"),
/// so the joined emotion + pacing label is sent as-is; there is deliberately
/// no keyword vocabulary deciding which labels may pass. The only filters are
/// mechanical, in <see cref="EmotionLabel"/>: no label, no tag.
/// </summary>
public static class EmotionTagMap
{
    /// <summary>The bracketed tags for a label: one combined tag, or empty when there is no usable label.</summary>
    public static IReadOnlyList<string> TagsForLabel(EmotionLabel label)
    {
        string? tag = ForLabel(label);
        return tag is null ? [] : [tag];
    }

    /// <summary>The label as a single bracketed tag, or null when the label is empty or not label-shaped.</summary>
    public static string? ForLabel(EmotionLabel label)
    {
        return label.IsEmpty ? null : $"[{label.Sanitized}]";
    }
}
