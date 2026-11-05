using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestParsing;
using static RequestValidation;

// Endpoint handlers for the HTTP API. Program.cs maps each one by name.
internal static partial class ApiEndpoints
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
