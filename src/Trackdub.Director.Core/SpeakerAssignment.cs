namespace Trackdub.Director.Core;

/// <summary>Resolves a source diarization label through the user's explicit voice assignment.</summary>
public static class SpeakerAssignment
{
    public static string? Resolve(string? sourceSpeaker, IReadOnlyDictionary<string, string>? mapping)
    {
        if (string.IsNullOrWhiteSpace(sourceSpeaker))
        {
            return sourceSpeaker;
        }

        if (mapping is null)
        {
            return sourceSpeaker;
        }

        foreach ((string label, string voice) in mapping)
        {
            if (string.Equals(label, sourceSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(voice) ? sourceSpeaker : voice.Trim();
            }
        }

        return sourceSpeaker;
    }
}
