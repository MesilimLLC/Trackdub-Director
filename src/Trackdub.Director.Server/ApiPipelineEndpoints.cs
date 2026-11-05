using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Director.Core;
using static ApiResults;
using static AuditionOutputs;
using static RequestParsing;
using static RequestValidation;

internal static partial class ApiEndpoints
{
    internal static async Task<IResult> AuditionAsync(
        HttpRequest request,
        [AsParameters] AuditionEndpointServices services,
        CancellationToken ct)
    {
        var (client, tts, audioStore, idempotency, lifetime) = services;
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

        return await RunIdempotentAsync(new(
            request, idempotency, "audition:",
            () => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(req, RequestParsing.JsonOptionsWeb))),
            RunAuditionAsync, ct, lifetime.ApplicationStopping)).ConfigureAwait(false);

        Task<IResult> RunAuditionAsync(CancellationToken runCt)
        {
            return ApiHandlers.RunSerializedAsync(() => ApiHandlers.RunAuditionAsync(client, tts, auditionOptions, req, audioStore, runCt), runCt);
        }
    }

    internal static async Task<IResult> PipelineAsync(
        HttpRequest request,
        [AsParameters] PipelineEndpointServices services,
        CancellationToken ct)
    {
        var (client, transcribeClient, tts, idempotency, audioStore, lifetime) = services;
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

        return await RunIdempotentAsync(new(
            request, idempotency, string.Empty, () => BuildPipelineFingerprint(req, audio),
            RunPipelineAsync, ct, lifetime.ApplicationStopping)).ConfigureAwait(false);

        Task<IResult> RunPipelineAsync(CancellationToken runCt)
        {
            return ApiHandlers.RunSerializedAsync(
                () => ApiHandlers.RunPipelineWithReasoningAsync(client, transcribeClient, tts, options, pipelineRequest, audioStore, runCt), runCt);
        }
    }

    private sealed record IdempotentExecution(
        HttpRequest Request,
        PipelineIdempotencyStore Store,
        string Prefix,
        Func<string> Fingerprint,
        Func<CancellationToken, Task<IResult>> Execute,
        CancellationToken RequestCancellation,
        CancellationToken Shutdown);

    private static Task<IResult> RunIdempotentAsync(IdempotentExecution execution)
    {
        string? key = execution.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (key is null) return execution.Execute(execution.RequestCancellation);
        if (!PipelineIdempotencyStore.TryParseKey(key, out _))
        {
            return Task.FromResult(Results.BadRequest(new { error = "Idempotency-Key must be a timestamped UUID." }) as IResult);
        }

        return execution.Store.RunAsync(execution.Prefix + key, execution.Fingerprint(), () => execution.Execute(execution.Shutdown));
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
}

internal sealed record AuditionEndpointServices(
    MetaClient Client,
    ITtsProvider Tts,
    AuditionAudioStore AudioStore,
    PipelineIdempotencyStore Idempotency,
    IHostApplicationLifetime Lifetime);

internal sealed record PipelineEndpointServices(
    MetaClient Client,
    IMuseTranscribeClient TranscribeClient,
    ITtsProvider Tts,
    PipelineIdempotencyStore Idempotency,
    AuditionAudioStore AudioStore,
    IHostApplicationLifetime Lifetime);
