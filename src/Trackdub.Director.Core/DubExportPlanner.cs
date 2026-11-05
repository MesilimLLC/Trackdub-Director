using System.Globalization;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Validates the export contract before ffmpeg is invoked: every expected
/// segment needs exactly one WAV winner and an explicit start, measured winner
/// audio must fit inside the source video, and placements may not overlap.
/// </summary>
public static partial class DubExportPlanner
{
    public const int MaxSegments = 100;
    public const int MaxAudioBytesPerSegment = 16 * 1024 * 1024;
    public const long MaxTotalAudioBytes = 64L * 1024 * 1024;
    private const double ToleranceSeconds = 0.000001;

    private static readonly PcmFormat WavProbeFormat = new() { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };

    public static DubExportPlan Build(
        IReadOnlyList<string> expectedSegmentIds,
        IReadOnlyList<DubExportSegmentInput> segments,
        double videoDurationSeconds)
    {
        string[] expected = ValidateInputs(expectedSegmentIds, segments, videoDurationSeconds);
        var byId = IndexSegments(segments);
        ValidateSegmentIds(expected, byId);
        DubExportPlacement[] placements = BuildPlacements(expected, byId);
        ValidateTimeline(placements, videoDurationSeconds);
        return new DubExportPlan(placements);
    }

    private static string[] ValidateInputs(
        IReadOnlyList<string> expectedSegmentIds,
        IReadOnlyList<DubExportSegmentInput> segments,
        double videoDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectedSegmentIds);
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(videoDurationSeconds) || videoDurationSeconds <= 0)
        {
            throw new ArgumentException("Source video duration must be positive.", nameof(videoDurationSeconds));
        }

        string[] expected = expectedSegmentIds.Select(id => id?.Trim() ?? string.Empty).ToArray();
        ValidateExpectedIds(expected);
        return expected;
    }

    private static void ValidateExpectedIds(string[] expected)
    {
        if (expected.Length == 0 || expected.Length > MaxSegments)
        {
            throw new ArgumentException($"Export requires 1 to {MaxSegments} expected segment ids.");
        }
        if (expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Expected segment ids must not be blank.");
        }
        if (expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
        {
            throw new ArgumentException("Expected segment ids must be unique.");
        }
    }

    private static Dictionary<string, DubExportSegmentInput> IndexSegments(IReadOnlyList<DubExportSegmentInput> segments)
    {
        var byId = new Dictionary<string, DubExportSegmentInput>(StringComparer.Ordinal);
        long totalAudioBytes = 0;
        foreach (DubExportSegmentInput segment in segments)
        {
            string id = RequireSegmentId(segment);
            if (!byId.TryAdd(id, segment))
            {
                throw new ArgumentException($"Duplicate export audio for segment '{id}'.", nameof(segments));
            }
            totalAudioBytes += segment.WavAudio?.Length ?? 0;
        }
        if (totalAudioBytes > MaxTotalAudioBytes)
        {
            throw new ArgumentException($"Winner audio exceeds the {MaxTotalAudioBytes} byte export limit.");
        }
        return byId;
    }

    private static string RequireSegmentId(DubExportSegmentInput segment)
    {
        return segment is null || string.IsNullOrWhiteSpace(segment.Id)
            ? throw new ArgumentException("Every export segment must have an id.")
            : segment.Id.Trim();
    }

    private static void ValidateSegmentIds(string[] expected, Dictionary<string, DubExportSegmentInput> byId)
    {
        string[] missing = expected.Where(id => !byId.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException($"Missing winning audio for segment(s): {string.Join(", ", missing)}.");
        }

        string[] extra = byId.Keys.Where(id => !expected.Contains(id, StringComparer.Ordinal)).ToArray();
        if (extra.Length > 0)
        {
            throw new ArgumentException($"Export audio supplied for unexpected segment(s): {string.Join(", ", extra)}.");
        }
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
