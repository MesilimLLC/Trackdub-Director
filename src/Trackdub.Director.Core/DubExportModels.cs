using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>One gate-winning take to place on the muted original-video timeline.</summary>
public sealed record DubExportSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("start_seconds")] double? StartSeconds,
    [property: JsonIgnore] byte[]? WavAudio);

/// <summary>One validated, measured placement in a dub export.</summary>
public sealed record DubExportPlacement(
    string Id,
    double StartSeconds,
    double DurationSeconds,
    byte[] WavAudio)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

/// <summary>A complete, non-overlapping winner timeline for one source video.</summary>
public sealed record DubExportPlan(IReadOnlyList<DubExportPlacement> Placements);

public interface IDubExportService
{
    Task<Stream> RenderAsync(
        Stream video,
        string videoFileName,
        IReadOnlyList<string> expectedSegmentIds,
        IReadOnlyList<DubExportSegmentInput> segments,
        CancellationToken cancellationToken = default);
}

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

    private static readonly PcmFormat WavProbeFormat = new() { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };

    public static DubExportPlan Build(
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
        if (expected.Length == 0 || expected.Length > MaxSegments || expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"Export requires 1 to {MaxSegments} expected segment ids.", nameof(expectedSegmentIds));
        }
        if (expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
        {
            throw new ArgumentException("Expected segment ids must be unique.", nameof(expectedSegmentIds));
        }

        var byId = new Dictionary<string, DubExportSegmentInput>(StringComparer.Ordinal);
        long totalAudioBytes = 0;
        foreach (DubExportSegmentInput segment in segments)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Id))
            {
                throw new ArgumentException("Every export segment must have an id.", nameof(segments));
            }
            string id = segment.Id.Trim();
            if (!byId.TryAdd(id, segment))
            {
                throw new ArgumentException($"Duplicate export audio for segment '{id}'.", nameof(segments));
            }
            totalAudioBytes += segment.WavAudio?.Length ?? 0;
        }

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
        if (totalAudioBytes > MaxTotalAudioBytes)
        {
            throw new ArgumentException($"Winner audio exceeds the {MaxTotalAudioBytes} byte export limit.");
        }

        var placements = new List<DubExportPlacement>(expected.Length);
        foreach (string id in expected)
        {
            DubExportSegmentInput segment = byId[id];
            if (segment.StartSeconds is not { } start || !double.IsFinite(start) || start < 0)
            {
                throw new ArgumentException($"Segment '{id}' needs an explicit start_seconds value of 0 or more.");
            }
            if (segment.WavAudio is not { Length: > 0 } wav || wav.Length > MaxAudioBytesPerSegment || !IsWav(wav))
            {
                throw new ArgumentException($"Segment '{id}' needs a non-empty WAV winner no larger than {MaxAudioBytesPerSegment} bytes.");
            }
            double duration;
            try
            {
                duration = AudioDuration.Seconds(wav, WavProbeFormat);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException($"Segment '{id}' winner audio is not a usable WAV: {ex.Message}", ex);
            }
            if (!double.IsFinite(duration) || duration <= 0)
            {
                throw new ArgumentException($"Segment '{id}' winner audio has no positive measured duration.");
            }
            placements.Add(new DubExportPlacement(id, start, duration, wav));
        }

        placements.Sort((a, b) => a.StartSeconds != b.StartSeconds
            ? a.StartSeconds.CompareTo(b.StartSeconds)
            : string.CompareOrdinal(a.Id, b.Id));
        for (int i = 0; i < placements.Count; i++)
        {
            DubExportPlacement placement = placements[i];
            if (placement.EndSeconds > videoDurationSeconds + 0.000001)
            {
                throw new ArgumentException(
                    $"Segment '{placement.Id}' ends at {placement.EndSeconds:0.###}s, after the {videoDurationSeconds:0.###}s source video.");
            }
            if (i + 1 < placements.Count && placements[i + 1].StartSeconds < placement.EndSeconds - 0.000001)
            {
                throw new ArgumentException(
                    $"Segments '{placement.Id}' and '{placements[i + 1].Id}' overlap on the export timeline.");
            }
        }

        return new DubExportPlan(placements);
    }

    public static string BuildAudioFilter(DubExportPlan plan, double videoDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string wholeDuration = videoDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            long delayMs = checked((long)Math.Round(plan.Placements[i].StartSeconds * 1000, MidpointRounding.AwayFromZero));
            if (builder.Length > 0) builder.Append(';');
            builder.Append(CultureInfo.InvariantCulture,
                $"[{i + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay={delayMs}|{delayMs},apad=whole_dur={wholeDuration}[a{i}]");
        }
        if (plan.Placements.Count == 1)
        {
            builder.Append(CultureInfo.InvariantCulture, $";[a0]atrim=0:{wholeDuration}[aout]");
            return builder.ToString();
        }

        builder.Append(';');
        for (int i = 0; i < plan.Placements.Count; i++) builder.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        builder.Append(CultureInfo.InvariantCulture,
            $"amix=inputs={plan.Placements.Count}:duration=first:dropout_transition=0,atrim=0:{wholeDuration}[aout]");
        return builder.ToString();
    }

    private static bool IsWav(byte[] bytes)
    {
        return bytes.Length >= 12
            && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8);
    }
}
