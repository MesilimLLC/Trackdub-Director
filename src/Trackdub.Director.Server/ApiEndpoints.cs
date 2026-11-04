using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestParsing;
using static RequestValidation;

// Endpoint handlers for the HTTP API. Program.cs maps each one by name.
internal static class ApiEndpoints
{
    private const string EmptySegmentsMessage = "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }.";

    internal static IResult Status()
    {
        bool hasKey = HasApiKey();
        string model = MetaAuth.GetModel();
        string baseUrl = MetaAuth.GetBaseUrl();

        // Never include the key.
        return Results.Json(new StatusResponse(hasKey, model, baseUrl, MetaAuth.GetReasoningEffort() ?? MetaAuth.DefaultReasoningEffort));
    }

    internal static async Task<IResult> ModelsAsync(MetaClient client, CancellationToken ct)
    {
        return await ApiHandlers.GuardAsync(async () =>
        {
            IReadOnlyList<MetaClient.ModelInfo> models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            // Return shape the CLI's models command would show: array of { id, object, created, owned_by }
            var dto = models.Select(m => new ModelDto(m.Id, m.Object, m.Created, m.OwnedBy)).ToArray();
            return Results.Json(dto);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> TranslateAsync(HttpRequest request, TranslationService translationService, CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadTranslateRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req?.Segments is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = EmptySegmentsMessage });
        }

        if (req.Segments.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Text is null))
        {
            return Results.BadRequest(new { error = "Each segment must have a non-empty 'id' and 'text'." });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        IReadOnlyList<TranslationSegmentInput> segments = req.Segments;
        return await ApiHandlers.GuardAsync(async () =>
        {
            TranslationBatchOutput batch = await translationService.TranslateBatchAsync(segments, from, to, ct).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> QaAsync(HttpRequest request, MetaClient client, CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadQaRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req?.Segments is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = EmptySegmentsMessage });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        if (!TryBuildQaOptions(req, to, out QaOptions? options, out string? error))
        {
            return Results.BadRequest(new { error });
        }

        return await ApiHandlers.RunSerializedAsync(() => ApiHandlers.RunQaWithReasoningAsync(client, options, req, from, to, ct), ct).ConfigureAwait(false);
    }

    internal static Task<IResult> SceneContextAsync(HttpRequest request, SceneContextService sceneService, CancellationToken ct)
    {
        return RunSegmentBatchAsync(
            () => ReadSceneRequestAsync(request, ct),
            "Request must include a non-empty 'segments' array of { id, text?, frame_image_data_url?, concepts?, speaker_id?, speaker_label?, frame_id?, timecode_seconds?, frame_sha256?, vision_enrichment? }.",
            ValidateSceneSegments,
            segments => sceneService.RunAsync(segments, ct));
    }

    internal static async Task<IResult> DirectorAdaptationAsync(HttpRequest request, MetaClient client, CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadAdaptationRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req is null || req.Segments.Count == 0)
        {
            return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text?, translation?, duration_seconds?, context?, director_adaptation? }." });
        }

        string? validationError = ValidateAdaptationSegments(req.Segments);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        if (!TryBuildQaOptions(new QaApiRequest([], null, null, req.Options), to, out QaOptions? options, out string? optionsError))
        {
            return Results.BadRequest(new { error = optionsError });
        }

        IReadOnlyList<DirectorAdaptationSegmentInput> segments = req.Segments;
        return await ApiHandlers.RunSerializedAsync(
            () => ApiHandlers.GuardAsync(() => ApiHandlers.RunDirectorWithReasoningAsync(client, options, segments, from, to, ct)),
            ct).ConfigureAwait(false);
    }

    internal static Task<IResult> VisualLocalisationAsync(HttpRequest request, VisualLocalisationService service, CancellationToken ct)
    {
        return RunSegmentBatchAsync(
            () => ReadVisualLocalisationRequestAsync(request, ct),
            "Request must include a non-empty 'segments' array of { id, frame_image_data_url, box, source_text, target_text }.",
            ValidateVisualSegments,
            segments => service.RunAsync(segments, ct));
    }

    private static async Task<IResult> RunSegmentBatchAsync<TSegment, TBatch>(
        Func<Task<TSegment[]?>> read,
        string emptyMessage,
        Func<TSegment[], string?> validate,
        Func<TSegment[], Task<TBatch>> run)
    {
        var (segments, readError) = await ApiHandlers.ReadOrBadRequestAsync(read);
        if (readError is not null)
        {
            return readError;
        }

        if (segments is null || segments.Length == 0)
        {
            return Results.BadRequest(new { error = emptyMessage });
        }

        string? validationError = validate(segments);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        return await ApiHandlers.GuardAsync(async () =>
        {
            TBatch batch = await run(segments).ConfigureAwait(false);
            return Results.Json(batch);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> AuditionAsync(
        HttpRequest request,
        MetaClient client,
        ITtsProvider tts,
        AuditionAudioStore audioStore,
        PipelineIdempotencyStore idempotency,
        IHostApplicationLifetime lifetime,
        CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadAuditionRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req is null || req.Segments.Count == 0)
        {
            return Results.BadRequest(new { error = "Request must include a non-empty 'segments' array of { id, text, duration_seconds?, speaker_id? }." });
        }

        string? validationError = ValidateAuditionSegments(req.Segments);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        string auditionTo = ResolveLanguage(req.TargetLanguage, "es");
        if (!TryBuildQaOptions(new QaApiRequest([], null, null, req.Options), auditionTo, out QaOptions? auditionOptions, out string? auditionOptionsError))
        {
            return Results.BadRequest(new { error = auditionOptionsError });
        }

        string? idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault();
        if (idempotencyKey is not null && !PipelineIdempotencyStore.TryParseKey(idempotencyKey, out _))
        {
            return Results.BadRequest(new { error = "Idempotency-Key must be a timestamped UUID." });
        }

        if (idempotencyKey is null)
        {
            return await RunAuditionAsync(ct).ConfigureAwait(false);
        }

        byte[] requestBody = JsonSerializer.SerializeToUtf8Bytes(req, RequestParsing.JsonOptionsWeb);
        string fingerprint = Convert.ToHexString(SHA256.HashData(requestBody));
        return await idempotency.RunAsync("audition:" + idempotencyKey, fingerprint, () => RunAuditionAsync(lifetime.ApplicationStopping)).ConfigureAwait(false);

        Task<IResult> RunAuditionAsync(CancellationToken runCt)
        {
            return ApiHandlers.RunSerializedAsync(() => ApiHandlers.RunAuditionAsync(client, tts, auditionOptions, req, audioStore, runCt), runCt);
        }
    }

    internal static async Task<IResult> CopilotProposeAsync(HttpRequest request, CopilotService service, CancellationToken ct)
    {
        CopilotRequest? req;
        try
        {
            req = await request.ReadFromJsonAsync<CopilotRequest>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
        }

        if (req is null || string.IsNullOrWhiteSpace(req.Goal))
        {
            return Results.BadRequest(new { error = "Copilot request must include a goal and workbench state." });
        }

        return await ApiHandlers.GuardAsync(async () =>
        {
            CopilotProposal proposal = await service.ProposeAsync(req, ct).ConfigureAwait(false);
            return Results.Json(proposal);
        }).ConfigureAwait(false);
    }

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

        return await RenderExportAsync(exportService, video!, metadata.ExpectedIds, segments!, ct).ConfigureAwait(false);
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
        IFormFile video,
        string[] expectedIds,
        DubExportSegmentInput[] segments,
        CancellationToken ct)
    {
        try
        {
            await using Stream videoStream = video.OpenReadStream();
            var exportRequest = new DubExportRequest(videoStream, Path.GetFileName(video.FileName), expectedIds, segments);
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

    internal static async Task<IResult> TranscribeAsync(HttpRequest request, TranscribeService transcribeService, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Request must be multipart/form-data with a WAV file in the 'audio' field." });
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Results.BadRequest(new { error = $"Invalid form body: {ex.Message}" });
        }

        IFormFile? audio = form.Files.GetFile("audio");
        if (audio is null || audio.Length == 0)
        {
            return Results.BadRequest(new { error = "Request must include a non-empty WAV file in the 'audio' field." });
        }

        byte[] audioBytes;
        await using (Stream uploaded = audio.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await uploaded.CopyToAsync(buffer, ct).ConfigureAwait(false);
            audioBytes = buffer.ToArray();
        }

        string? formatError = WavAudioFormat.ValidateForTranscription(audioBytes);
        if (formatError is not null)
        {
            return Results.BadRequest(new { error = formatError });
        }

        return await ApiHandlers.GuardAsync(async () =>
        {
            await using Stream stream = new MemoryStream(audioBytes, writable: false);
            TranscribeResult result = await transcribeService.TranscribeAsync(stream, audio.FileName, ct).ConfigureAwait(false);
            return Results.Json(result);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> PipelineAsync(
        HttpRequest request,
        MetaClient client,
        IMuseTranscribeClient transcribeClient,
        ITtsProvider tts,
        PipelineIdempotencyStore idempotency,
        AuditionAudioStore audioStore,
        IHostApplicationLifetime lifetime,
        CancellationToken ct)
    {
        var (req, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadPipelineRequestAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (req is null)
        {
            return Results.BadRequest(new { error = "Request body must be a JSON object with 'audio_base64' or a 'segments' array." });
        }

        string? inputError = ValidatePipelineInput(req, out byte[]? audio);
        if (inputError is not null)
        {
            return Results.BadRequest(new { error = inputError });
        }

        string from = ResolveLanguage(req.SourceLanguage, "en");
        string to = ResolveLanguage(req.TargetLanguage, "es");

        var optionsReq = new QaApiRequest([], null, null, req.Options);
        if (!TryBuildQaOptions(optionsReq, to, out QaOptions? options, out string? optionsError))
        {
            return Results.BadRequest(new { error = optionsError });
        }

        var pipelineRequest = ToPipelineRequest(req, audio, from, to);

        string? idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault();
        if (idempotencyKey is not null && !PipelineIdempotencyStore.TryParseKey(idempotencyKey, out _))
        {
            return Results.BadRequest(new { error = "Idempotency-Key must be a timestamped UUID." });
        }

        if (idempotencyKey is null)
        {
            return await RunPipelineAsync(ct).ConfigureAwait(false);
        }

        string fingerprint = BuildPipelineFingerprint(req, audio);
        return await idempotency.RunAsync(idempotencyKey, fingerprint, () => RunPipelineAsync(lifetime.ApplicationStopping)).ConfigureAwait(false);

        Task<IResult> RunPipelineAsync(CancellationToken runCt)
        {
            return ApiHandlers.RunSerializedAsync(
                () => ApiHandlers.RunPipelineWithReasoningAsync(client, transcribeClient, tts, options, pipelineRequest, audioStore, runCt), runCt);
        }
    }

    internal static IResult AuditionAudio(string id, AuditionAudioStore audioStore)
    {
        string? path = audioStore.ResolvePath(id);
        return path is null
            ? Results.NotFound(new { error = "Audition audio is unavailable or expired." })
            : Results.File(path, "audio/wav", enableRangeProcessing: true);
    }

    private static string BuildPipelineFingerprint(PipelineApiRequest request, byte[]? audio)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(request with { AudioBase64 = null }, RequestParsing.JsonOptionsWeb);
        byte[] audioHash = audio is null ? [] : SHA256.HashData(audio);
        byte[] combined = new byte[metadata.Length + audioHash.Length];
        metadata.CopyTo(combined, 0);
        audioHash.CopyTo(combined, metadata.Length);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    private static PipelineRequest ToPipelineRequest(PipelineApiRequest req, byte[]? audio, string from, string to)
    {
        return new PipelineRequest(
            audio,
            audio is null ? req.Segments : null,
            req.SceneSegments,
            Scene: req.Scene ?? true,
            Audition: req.Audition ?? false,
            SourceLanguage: from,
            TargetLanguage: to,
            AudioFileName: req.AudioFileName,
            SpeedRetake: req.SpeedRetake,
            EmotionTags: req.EmotionTags,
            SpeakerMapping: req.SpeakerMapping);
    }

    // Validates audio/segments/scene input; decodes audio when present. Returns an error message or null.
    private static string? ValidatePipelineInput(PipelineApiRequest req, out byte[]? audio)
    {
        audio = null;
        if (!string.IsNullOrWhiteSpace(req.AudioBase64))
        {
            string? audioError = TryDecodeAudio(req.AudioBase64, out audio);
            if (audioError is not null)
            {
                return audioError;
            }
        if (audio is not null && WavAudioFormat.ValidateForTranscription(audio) is { } formatError)
        {
            return formatError;
        }
        }

        string? segmentError = audio is null ? ValidatePipelineSegments(req.Segments) : null;
        if (segmentError is not null)
        {
            return segmentError;
        }

        return ValidatePipelineScene(req.SceneSegments);
    }

    // Framed scene results are matched back to lines by id, so duplicate framed ids would
    // hand an earlier line a later line's scene context.
    private static string? ValidatePipelineScene(IReadOnlyList<SceneSegmentInput>? sceneSegments)
    {
        if (sceneSegments is not { Count: > 0 })
        {
            return null;
        }

        string? sceneError = ValidateSceneSegments(sceneSegments.ToArray());
        if (sceneError is not null)
        {
            return sceneError;
        }

        bool hasDuplicateFramedId = sceneSegments
            .Where(s => !string.IsNullOrWhiteSpace(s.FrameImageDataUrl))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Any(g => g.Count() > 1);
        return hasDuplicateFramedId ? "Framed scene segment ids must be unique." : null;
    }

    private static string? TryDecodeAudio(string base64, out byte[]? audio)
    {
        try
        {
            audio = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            audio = null;
            return "'audio_base64' is not valid base64.";
        }

        return audio.Length == 0 ? "'audio_base64' decoded to zero bytes." : null;
    }

    private static string? ValidatePipelineSegments(IReadOnlyList<TranscribeSegment>? segments)
    {
        if (segments is not { Count: > 0 })
        {
            return "Pipeline needs 'audio_base64' or a non-empty 'segments' array of { id, text, duration_seconds, speaker? }.";
        }

        if (segments.Any(s => s is null))
        {
            return "segments must not contain null elements.";
        }

        if (segments.Any(HasMissingIdOrText))
        {
            return "Each segment must have a non-empty 'id' and 'text'.";
        }

        return segments.Count > 100 ? "At most 100 segments are allowed per request." : null;
    }

    private static bool HasMissingIdOrText(TranscribeSegment s) => string.IsNullOrWhiteSpace(s.Id) || s.Text is null;

    // SPA fallback: for non-/api routes, serve index.html if it exists (after a prod build).
    internal static async Task FallbackAsync(HttpContext ctx)
    {
        // Don't hijack API 404s
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { error = "Not found" }, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        var environment = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        string indexPath = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "index.html");
        if (File.Exists(indexPath))
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.SendFileAsync(indexPath, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        // No built frontend: return a minimal hint
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html";
        await ctx.Response.WriteAsync("""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Trackdub Director</title></head>
            <body style="font-family:system-ui;background:#111;color:#eee;padding:2rem">
            <h1>Trackdub Director — API running</h1>
            <p>Frontend not built. Run <code>npm run build</code> in <code>web/</code> or start Vite with <code>npm run dev</code>.</p>
            <p>API: <a style="color:#8be" href="/api/status">GET /api/status</a></p>
            </body></html>
            """, ctx.RequestAborted).ConfigureAwait(false);
    }
}
