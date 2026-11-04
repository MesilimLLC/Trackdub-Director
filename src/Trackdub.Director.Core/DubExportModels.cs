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

/// <summary>Everything needed to render one muted-original dubbed MP4.</summary>
public sealed record DubExportRequest(
    Stream Video,
    string VideoFileName,
    IReadOnlyList<string> ExpectedSegmentIds,
    IReadOnlyList<DubExportSegmentInput> Segments);

/// <summary>Temporary files used for one ffmpeg dub export.</summary>
public sealed record DubExportRenderFiles(
    string VideoPath,
    IReadOnlyList<string> AudioPaths,
    string OutputPath,
    double VideoDurationSeconds);

public interface IDubExportService
{
    Task<Stream> RenderAsync(DubExportRequest request, CancellationToken cancellationToken = default);
}
