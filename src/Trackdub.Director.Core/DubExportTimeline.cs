using System.Globalization;
using System.Text;

namespace Trackdub.Director.Core;

public static partial class DubExportPlanner
{
    private static DubExportPlacement[] BuildPlacements(
        string[] expected,
        Dictionary<string, DubExportSegmentInput> byId)
    {
        DubExportPlacement[] placements = expected.Select(id => BuildPlacement(new(id, byId[id]))).ToArray();
        Array.Sort(placements, ComparePlacements);
        return placements;
    }

    private static int ComparePlacements(DubExportPlacement a, DubExportPlacement b)
    {
        int byStart = a.StartSeconds.CompareTo(b.StartSeconds);
        return byStart != 0 ? byStart : string.CompareOrdinal(a.Id, b.Id);
    }

    private sealed record SegmentForExport(string Id, DubExportSegmentInput Segment);
    private sealed record WinnerAudio(string SegmentId, byte[] Bytes);

    private static DubExportPlacement BuildPlacement(SegmentForExport entry)
    {
        double start = RequireStart(entry);
        WinnerAudio wav = RequireWav(entry);
        return new DubExportPlacement(entry.Id, start, MeasureDuration(wav), wav.Bytes);
    }

    private static double RequireStart(SegmentForExport entry)
    {
        return entry.Segment.StartSeconds is { } start && double.IsFinite(start) && start >= 0
            ? start
            : throw new ArgumentException($"Segment '{entry.Id}' needs an explicit start_seconds value of 0 or more.");
    }

    private static WinnerAudio RequireWav(SegmentForExport entry)
    {
        var wav = new WinnerAudio(entry.Id, entry.Segment.WavAudio ?? []);
        return wav.Bytes.Length <= MaxAudioBytesPerSegment && IsWav(wav)
            ? wav
            : throw new ArgumentException($"Segment '{entry.Id}' needs a non-empty WAV winner no larger than {MaxAudioBytesPerSegment} bytes.");
    }

    private static double MeasureDuration(WinnerAudio wav)
    {
        double duration;
        try
        {
            duration = AudioDuration.Seconds(wav.Bytes, WavProbeFormat);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException($"Segment '{wav.SegmentId}' winner audio is not a usable WAV: {ex.Message}", ex);
        }
        return double.IsFinite(duration) && duration > 0
            ? duration
            : throw new ArgumentException($"Segment '{wav.SegmentId}' winner audio has no positive measured duration.");
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

    private static bool IsWav(WinnerAudio audio) => audio.Bytes.Length >= 12 && HasRiffHeader(audio) && HasWaveHeader(audio);

    private static bool HasRiffHeader(WinnerAudio audio) => audio.Bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8);

    private static bool HasWaveHeader(WinnerAudio audio) => audio.Bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8);
}
