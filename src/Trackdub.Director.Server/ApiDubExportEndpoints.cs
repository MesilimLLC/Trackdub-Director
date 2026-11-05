using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestParsing;
using static RequestValidation;

internal static partial class ApiEndpoints
{
    internal static async Task<IResult> ExportDubAsync(HttpRequest request, IDubExportService exportService, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Request must be multipart/form-data with 'video', 'segment_ids', 'placements', and winner WAV fields." });
        }

        SetExportBodyLimit(request);
        var (form, formError) = await ReadExportFormAsync(request, ct).ConfigureAwait(false);
        if (formError is not null) return formError;

        var (video, videoError) = RequireExportVideo(form!);
        if (videoError is not null) return videoError;

        var (metadata, metadataError) = ReadExportMetadata(form!);
        if (metadataError is not null) return metadataError;

        var (segments, segmentsError) = await ReadExportSegmentsAsync(form!, metadata!.Placements, ct).ConfigureAwait(false);
        if (segmentsError is not null) return segmentsError;

        return await RenderExportAsync(exportService, new(video!, metadata.ExpectedIds, segments!), ct).ConfigureAwait(false);
    }

    private static void SetExportBodyLimit(HttpRequest request)
    {
        var bodyLimit = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 272L * 1024 * 1024;
    }

    private static async Task<(IFormCollection? Form, IResult? Error)> ReadExportFormAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return (await request.ReadFormAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return (null, Results.BadRequest(new { error = $"Invalid form body: {ex.Message}" }));
        }
    }

    private static (IFormFile? Video, IResult? Error) RequireExportVideo(IFormCollection form)
    {
        IFormFile? video = form.Files.GetFile("video");
        if (video is null || video.Length == 0)
        {
            return (null, Results.BadRequest(new { error = "Request must include a non-empty original video in the 'video' field." }));
        }
        return video.Length <= FfmpegDubExportService.MaxVideoBytes
            ? (video, null)
            : (null, Results.BadRequest(new { error = $"Original video exceeds the {FfmpegDubExportService.MaxVideoBytes} byte export limit." }));
    }

    private sealed record ExportMetadata(string[] ExpectedIds, DubExportPlacementDto[] Placements);

    private static (ExportMetadata? Metadata, IResult? Error) ReadExportMetadata(IFormCollection form)
    {
        try
        {
            string[] expected = DeserializeExportField<string[]>(form, "segment_ids", "Missing segment_ids.");
            DubExportPlacementDto[] placements = DeserializeExportField<DubExportPlacementDto[]>(form, "placements", "Missing placements.");
            return (new ExportMetadata(expected, placements), null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return (null, Results.BadRequest(new { error = $"Invalid export metadata: {ex.Message}" }));
        }
    }

    private static T DeserializeExportField<T>(IFormCollection form, string field, string missingMessage)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(form[field].ToString(), RequestParsing.JsonOptionsWeb)
            ?? throw new InvalidOperationException(missingMessage);
    }

    private static async Task<(DubExportSegmentInput[]? Segments, IResult? Error)> ReadExportSegmentsAsync(
        IFormCollection form,
        DubExportPlacementDto[] placements,
        CancellationToken ct)
    {
        var segments = new List<DubExportSegmentInput>(placements.Length);
        long totalAudioBytes = 0;
        foreach (DubExportPlacementDto placement in placements)
        {
            if (placement is null)
            {
                return (null, Results.BadRequest(new { error = "Export placements must not contain null entries." }));
            }
            var (segment, error) = await ReadExportSegmentAsync(form, placement, totalAudioBytes, ct).ConfigureAwait(false);
            if (error is not null) return (null, error);
            totalAudioBytes += segment!.WavAudio!.Length;
            segments.Add(segment);
        }
        return (segments.ToArray(), null);
    }

    private static async Task<(DubExportSegmentInput? Segment, IResult? Error)> ReadExportSegmentAsync(
        IFormCollection form,
        DubExportPlacementDto placement,
        long totalAudioBytes,
        CancellationToken ct)
    {
        var (audio, error) = RequireExportAudio(form, placement, totalAudioBytes);
        if (error is not null) return (null, error);

        await using Stream audioStream = audio!.OpenReadStream();
        using var copy = new MemoryStream();
        await audioStream.CopyToAsync(copy, ct).ConfigureAwait(false);
        return (new DubExportSegmentInput(placement.Id, placement.StartSeconds, copy.ToArray()), null);
    }

    private static (IFormFile? Audio, IResult? Error) RequireExportAudio(
        IFormCollection form,
        DubExportPlacementDto placement,
        long totalAudioBytes)
    {
        if (string.IsNullOrWhiteSpace(placement.AudioField))
        {
            return (null, Results.BadRequest(new { error = $"Placement for segment '{placement.Id}' is missing audio_field." }));
        }

        IFormFile? audio = form.Files.GetFile(placement.AudioField);
        if (audio is null || audio.Length == 0)
        {
            return (null, Results.BadRequest(new { error = $"Missing winning audio for segment '{placement.Id}'." }));
        }
        if (audio.Length > DubExportPlanner.MaxAudioBytesPerSegment)
        {
            return (null, Results.BadRequest(new { error = $"Winning audio for segment '{placement.Id}' exceeds the per-segment export limit." }));
        }
        return totalAudioBytes + audio.Length <= DubExportPlanner.MaxTotalAudioBytes
            ? (audio, null)
            : (null, Results.BadRequest(new { error = "Winner audio exceeds the total export limit." }));
    }

    private static async Task<IResult> RenderExportAsync(
        IDubExportService exportService,
        ExportRenderInput input,
        CancellationToken ct)
    {
        try
        {
            await using Stream videoStream = input.Video.OpenReadStream();
            var exportRequest = new DubExportRequest(videoStream, Path.GetFileName(input.Video.FileName), input.ExpectedIds, input.Segments);
            Stream rendered = await exportService.RenderAsync(exportRequest, ct).ConfigureAwait(false);
            return Results.Stream(rendered, "video/mp4", "trackdub-dub.mp4");
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private sealed record ExportRenderInput(IFormFile Video, string[] ExpectedIds, DubExportSegmentInput[] Segments);
}
