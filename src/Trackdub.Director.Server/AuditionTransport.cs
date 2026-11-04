using Trackdub.Director.Core;

// Browser transport only: attach playable WAV data URLs to a copy of an
// audition report after any on-disk writer has run. Core reports/manifests
// stay filename-based. Winners get first claim on the aggregate byte budget.
internal static class AuditionTransport
{
    internal const int MaxAudioTransportBytes = 16 * 1024 * 1024;

    internal static AuditionReport Enrich(AuditionReport report)
    {
        var candidates = report.Segments
            .SelectMany(segment => segment.Takes.Select(take => (segment.Id, Take: take)))
            .Where(candidate => candidate.Take.AudioBytes is { Length: > 0 })
            .OrderByDescending(candidate => candidate.Take.IsWinner)
            .ToList();

        var included = new HashSet<AuditionTakeReport>(ReferenceEqualityComparer.Instance);
        var omitted = new HashSet<AuditionTakeReport>(ReferenceEqualityComparer.Instance);
        long used = 0;
        foreach ((string _, AuditionTakeReport take) in candidates)
        {
            int length = take.AudioBytes!.Length;
            if (used + length <= MaxAudioTransportBytes)
            {
                included.Add(take);
                used += length;
            }
            else
            {
                omitted.Add(take);
            }
        }

        AuditionSegmentReport[] segments = report.Segments.Select(segment => segment with
        {
            Takes = segment.Takes.Select(take =>
            {
                if (included.Contains(take))
                {
                    return take with
                    {
                        AudioDataUrl = "data:audio/wav;base64," + Convert.ToBase64String(take.AudioBytes!),
                        AudioTransportOmitted = null,
                    };
                }

                return omitted.Contains(take)
                    ? take with { AudioDataUrl = null, AudioTransportOmitted = "response_audio_limit" }
                    : take;
            }).ToArray(),
        }).ToArray();

        return report with { Segments = segments };
    }
}
