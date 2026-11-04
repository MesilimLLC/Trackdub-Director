using System.Globalization;
using System.Text;

namespace Trackdub.Director.Core;

/// <summary>
/// Validates the export contract before ffmpeg is invoked: every expected
/// segment needs exactly one WAV winner and an explicit start, measured winner
/// audio must fit inside the source video, and placements may not overlap.
/// </summary>
public static class DubExportPlanner
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

    public static string BuildAudioFilter(DubExportPlan plan, double videoDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string wholeDuration = FormatSeconds(videoDurationSeconds);
        string filter = string.Join(";", plan.Placements.Select(PlacementFilter));
        return plan.Placements.Count == 1
            ? $"{filter};[a0]atrim=0:{wholeDuration}[aout]"
            : $"{filter};{BuildMixFilter(plan.Placements.Count, wholeDuration)}";

        string PlacementFilter(DubExportPlacement placement, int index)
        {
            long delayMs = checked((long)Math.Round(placement.StartSeconds * 1000, MidpointRounding.AwayFromZero));
            return $"[{index + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,"
                + $"adelay={delayMs}|{delayMs},apad=whole_dur={wholeDuration}[a{index}]";
        }
    }

    private static string BuildMixFilter(int count, string wholeDuration)
    {
        var inputs = new StringBuilder();
        for (int i = 0; i < count; i++) inputs.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        return $"{inputs}amix=inputs={count}:duration=longest:normalize=0:dropout_transition=0,atrim=0:{wholeDuration}[aout]";
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

    private static DubExportPlacement[] BuildPlacements(
        string[] expected,
        Dictionary<string, DubExportSegmentInput> byId)
    {
        DubExportPlacement[] placements = expected.Select(id => BuildPlacement(id, byId[id])).ToArray();
        Array.Sort(placements, ComparePlacements);
        return placements;
    }

    private static int ComparePlacements(DubExportPlacement a, DubExportPlacement b)
    {
        int byStart = a.StartSeconds.CompareTo(b.StartSeconds);
        return byStart != 0 ? byStart : string.CompareOrdinal(a.Id, b.Id);
    }

    private static DubExportPlacement BuildPlacement(string id, DubExportSegmentInput segment)
    {
        double start = RequireStart(id, segment);
        byte[] wav = RequireWav(id, segment);
        return new DubExportPlacement(id, start, MeasureDuration(id, wav), wav);
    }

    private static double RequireStart(string id, DubExportSegmentInput segment)
    {
        return segment.StartSeconds is { } start && double.IsFinite(start) && start >= 0
            ? start
            : throw new ArgumentException($"Segment '{id}' needs an explicit start_seconds value of 0 or more.");
    }

    private static byte[] RequireWav(string id, DubExportSegmentInput segment)
    {
        return segment.WavAudio is { Length: > 0 } wav && wav.Length <= MaxAudioBytesPerSegment && IsWav(wav)
            ? wav
            : throw new ArgumentException($"Segment '{id}' needs a non-empty WAV winner no larger than {MaxAudioBytesPerSegment} bytes.");
    }

    private static double MeasureDuration(string id, byte[] wav)
    {
        double duration;
        try
        {
            duration = AudioDuration.Seconds(wav, WavProbeFormat);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException($"Segment '{id}' winner audio is not a usable WAV: {ex.Message}", ex);
        }
        return double.IsFinite(duration) && duration > 0
            ? duration
            : throw new ArgumentException($"Segment '{id}' winner audio has no positive measured duration.");
    }

    private static void ValidateTimeline(DubExportPlacement[] placements, double videoDurationSeconds)
    {
        for (int i = 0; i < placements.Length; i++)
        {
            ValidatePlacementEnd(placements[i], videoDurationSeconds);
            if (i + 1 < placements.Length) ValidateNoOverlap(placements[i], placements[i + 1]);
        }
    }

    private static void ValidatePlacementEnd(DubExportPlacement placement, double videoDurationSeconds)
    {
        if (placement.EndSeconds > videoDurationSeconds + ToleranceSeconds)
        {
            throw new ArgumentException(
                $"Segment '{placement.Id}' ends at {FormatSeconds(placement.EndSeconds)}s, after the {FormatSeconds(videoDurationSeconds)}s source video.");
        }
    }

    private static void ValidateNoOverlap(DubExportPlacement current, DubExportPlacement next)
    {
        if (next.StartSeconds < current.EndSeconds - ToleranceSeconds)
        {
            throw new ArgumentException($"Segments '{current.Id}' and '{next.Id}' overlap on the export timeline.");
        }
    }

    private static bool IsWav(byte[] bytes) => bytes.Length >= 12 && HasRiffHeader(bytes) && HasWaveHeader(bytes);

    private static bool HasRiffHeader(byte[] bytes) => bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8);

    private static bool HasWaveHeader(byte[] bytes) => bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8);

    private static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
